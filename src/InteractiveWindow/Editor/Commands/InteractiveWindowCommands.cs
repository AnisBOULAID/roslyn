// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.VisualStudio.InteractiveWindow.Commands
{
    internal sealed class Commands : IInteractiveWindowCommands
    {
        private const string _commandSeparator = ",";

        private readonly Dictionary<string, IInteractiveWindowCommand> _commands;
        private readonly int _maxCommandNameLength;
        private readonly IInteractiveWindow _window;
        private readonly IContentType _commandContentType;
        private readonly IStandardClassificationService _classificationRegistry;
        private IContentType _languageContentType;
        private ITextBuffer _previousBuffer;

        public string CommandPrefix { get; set; }

        public bool InCommand
        {
            get
            {
                return _window.CurrentLanguageBuffer.ContentType == _commandContentType;
            }
        }

        internal Commands(IInteractiveWindow window, string prefix, IEnumerable<IInteractiveWindowCommand> commands, IContentTypeRegistryService contentTypeRegistry = null, IStandardClassificationService classificationRegistry = null)
        {
            CommandPrefix = prefix;
            _window = window;

            Dictionary<string, IInteractiveWindowCommand> commandsDict = new Dictionary<string, IInteractiveWindowCommand>();
            foreach (var command in commands)
            {
                int length = 0;
                foreach (var name in command.Names)
                {
                    if (commandsDict.ContainsKey(name))
                    {
                        throw new InvalidOperationException(string.Format(InteractiveWindowResources.DuplicateCommand, string.Join(", ", command.Names)));
                    }
                    if (length != 0)
                    {
                        length += _commandSeparator.Length;
                    }
                    length += name.Length;

                    commandsDict[name] = command;
                }
                if (length == 0) {
                    throw new InvalidOperationException(string.Format(InteractiveWindowResources.MissingCommandName, command.GetType().Name));
                }
                _maxCommandNameLength = Math.Max(_maxCommandNameLength, length);
            }

            _commands = commandsDict;

            _classificationRegistry = classificationRegistry;

            if (contentTypeRegistry != null)
            {
                _commandContentType = contentTypeRegistry.GetContentType(PredefinedInteractiveCommandsContentTypes.InteractiveCommandContentTypeName);
            }

            if (window != null)
            {
                window.SubmissionBufferAdded += Window_SubmissionBufferAdded;
                window.Properties[typeof(IInteractiveWindowCommands)] = this;
            }
        }

        private void Window_SubmissionBufferAdded(object sender, SubmissionBufferAddedEventArgs e)
        {
            if (_previousBuffer != null)
            {
                _previousBuffer.Changed -= NewBufferChanged;
            }

            _languageContentType = e.NewBuffer.ContentType;
            e.NewBuffer.Changed += NewBufferChanged;
            _previousBuffer = e.NewBuffer;
        }

        private void NewBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            bool isCommand = IsCommand(e.After.GetExtent());

            ITextBuffer buffer = e.After.TextBuffer;
            IContentType contentType = buffer.ContentType;
            IContentType newContentType = null;

            if (contentType == _languageContentType)
            {
                if (isCommand)
                {
                    newContentType = _commandContentType;
                }
            }
            else
            {
                if (!isCommand)
                {
                    newContentType = _languageContentType;
                }
            }

            if (newContentType != null)
            {
                buffer.ChangeContentType(newContentType, editTag: null);
            }
        }

        internal bool IsCommand(SnapshotSpan span)
        {
            SnapshotSpan prefixSpan, commandSpan, argumentsSpan;
            return TryParseCommand(span, out prefixSpan, out commandSpan, out argumentsSpan) != null;
        }

        internal IInteractiveWindowCommand TryParseCommand(SnapshotSpan span, out SnapshotSpan prefixSpan, out SnapshotSpan commandSpan, out SnapshotSpan argumentsSpan)
        {
            string prefix = CommandPrefix;

            SnapshotSpan trimmed = span.TrimStart();
            if (!trimmed.StartsWith(prefix))
            {
                prefixSpan = commandSpan = argumentsSpan = default(SnapshotSpan);
                return null;
            }

            prefixSpan = trimmed.SubSpan(0, prefix.Length);
            var nameAndArgs = trimmed.SubSpan(prefix.Length).TrimStart();
            SnapshotPoint nameEnd = nameAndArgs.IndexOfAnyWhiteSpace() ?? span.End;
            commandSpan = new SnapshotSpan(span.Snapshot, Span.FromBounds(nameAndArgs.Start.Position, nameEnd.Position));

            argumentsSpan = new SnapshotSpan(span.Snapshot, Span.FromBounds(nameEnd.Position, span.End.Position)).Trim();

            return this[commandSpan.GetText()];
        }

        public IInteractiveWindowCommand this[string name]
        {
            get
            {
                IInteractiveWindowCommand command;
                _commands.TryGetValue(name, out command);
                return command;
            }
        }

        public IEnumerable<IInteractiveWindowCommand> GetCommands()
        {
            return _commands.Values;
        }

        internal IEnumerable<string> Help()
        {
            string format = "{0,-" + _maxCommandNameLength + "}  {1}";
            return _commands.OrderBy(entry => entry.Key).Select(cmd => string.Format(format, cmd.Key, cmd.Value.Description));
        }

        public IEnumerable<ClassificationSpan> Classify(SnapshotSpan span)
        {
            SnapshotSpan prefixSpan, commandSpan, argumentsSpan;
            var command = TryParseCommand(span.Snapshot.GetExtent(), out prefixSpan, out commandSpan, out argumentsSpan);
            if (command == null)
            {
                yield break;
            }

            if (span.OverlapsWith(prefixSpan))
            {
                yield return Classification(span.Snapshot, prefixSpan, _classificationRegistry.Keyword);
            }

            if (span.OverlapsWith(commandSpan))
            {
                yield return Classification(span.Snapshot, commandSpan, _classificationRegistry.Keyword);
            }

            if (argumentsSpan.Length > 0)
            {
                foreach (var classifiedSpan in command.ClassifyArguments(span.Snapshot, argumentsSpan.Span, span.Span))
                {
                    yield return classifiedSpan;
                }
            }
        }

        private ClassificationSpan Classification(ITextSnapshot snapshot, Span span, IClassificationType classificationType)
        {
            return new ClassificationSpan(new SnapshotSpan(snapshot, span), classificationType);
        }

        public Task<ExecutionResult> TryExecuteCommand()
        {
            var span = _window.CurrentLanguageBuffer.CurrentSnapshot.GetExtent();

            SnapshotSpan prefixSpan, commandSpan, argumentsSpan;
            var command = TryParseCommand(span, out prefixSpan, out commandSpan, out argumentsSpan);
            if (command == null)
            {
                return null;
            }

            try
            {
                return command.Execute(_window, argumentsSpan.GetText()) ?? ExecutionResult.Failed;
            }
            catch (Exception e)
            {
                _window.ErrorOutputWriter.WriteLine(string.Format("Command '{0}' failed: {1}", command.Names.First(), e.Message));
                return ExecutionResult.Failed;
            }
        }

        private const string HelpIndent = "  ";

        private static readonly string[] s_shortcutDescriptions = new[]
        {
"Enter                Evaluate the current input if it appears to be complete.",
"Ctrl-Enter           If the caret is in current pending input submission, evaluate the entire submission.",
"                     If the caret is in a previous input block, copy that input text to the end of the buffer.",
"Shift-Enter          If the caret is in the current pending input submission, insert a new line.",
"Escape               If the caret is in the current pending input submission, delete the entire submission.",
"Alt-UpArrow          Paste previous input at end of buffer, rotate through history.",
"Alt-DownArrow        Paste next input at end of buffer, rotate through history.",
"UpArrow              Normal editor buffer navigation.",
"DownArrow            Normal editor buffer navigation.",
"Ctrl-K, Ctrl-Enter   Paste the selection at the end of interactive buffer, leave caret at the end of input.",
"Ctrl-E, Ctrl-Enter   Paste and execute the selection before any pending input in the interactive buffer.",
"Ctrl-A               Alternatively select the input block containing the caret, or whole buffer.",
        };

        public void DisplayHelp()
        {
            _window.WriteLine("Keyboard shortcuts:");
            foreach (var line in s_shortcutDescriptions)
            {
                _window.Write(HelpIndent);
                _window.WriteLine(line);
            }

            _window.WriteLine("REPL commands:");
            foreach (var line in Help())
            {
                _window.Write(HelpIndent);
                _window.WriteLine(line);
            }
        }

        public void DisplayCommandUsage(IInteractiveWindowCommand command, TextWriter writer, bool displayDetails)
        {
            if (displayDetails)
            {
                writer.WriteLine(command.Description);
                writer.WriteLine(string.Empty);
            }

            writer.WriteLine("Usage:");
            writer.Write(HelpIndent);
            writer.Write(CommandPrefix);
            writer.Write(string.Join(_commandSeparator, command.Names));

            string commandLine = command.CommandLine;
            if (commandLine != null)
            {
                writer.Write(" ");
                writer.Write(commandLine);
            }

            if (displayDetails)
            {
                writer.WriteLine(string.Empty);

                var paramsDesc = command.ParametersDescription;
                if (paramsDesc != null && paramsDesc.Any())
                {
                    writer.WriteLine(string.Empty);
                    writer.WriteLine("Parameters:");

                    int maxParamNameLength = paramsDesc.Max(entry => entry.Key.Length);
                    string paramHelpLineFormat = HelpIndent + "{0,-" + maxParamNameLength + "}  {1}";

                    foreach (var paramDesc in paramsDesc)
                    {
                        writer.WriteLine(string.Format(paramHelpLineFormat, paramDesc.Key, paramDesc.Value));
                    }
                }

                IEnumerable<string> details = command.DetailedDescription;
                if (details != null && details.Any())
                {
                    writer.WriteLine(string.Empty);
                    foreach (var line in details)
                    {
                        writer.WriteLine(line);
                    }
                }
            }
        }

        public void DisplayCommandHelp(IInteractiveWindowCommand command)
        {
            DisplayCommandUsage(command, _window.OutputWriter, displayDetails: true);
        }
    }
}
