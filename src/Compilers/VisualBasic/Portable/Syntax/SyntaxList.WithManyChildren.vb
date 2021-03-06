﻿' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System
Imports System.Collections
Imports System.Collections.Generic
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Text
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.VisualBasic.Syntax

    Friend Partial Class SyntaxList

        Friend Class WithManyChildren
            Inherits SyntaxList

            Private ReadOnly _children As ArrayElement(Of SyntaxNode)()

            Friend Sub New(green As InternalSyntax.SyntaxList, parent As SyntaxNode, position As Integer)
                MyBase.New(green, parent, position)
                Me._children = New ArrayElement(Of SyntaxNode)(green.SlotCount - 1) {}
            End Sub

            Friend Overrides Function GetNodeSlot(index As Integer) As SyntaxNode
                Return GetRedElement(Me._children(index).Value, index)
            End Function

            Friend Overrides Function GetCachedSlot(i As Integer) As SyntaxNode
                Return TryCast(Me._children(i).Value, VisualBasicSyntaxNode)
            End Function
        End Class
    End Class
End Namespace
