﻿' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.ComponentModel.Composition
Imports System.Threading
Imports Microsoft.CodeAnalysis.Editor.Implementation.Highlighting
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.KeywordHighlighting
    <ExportHighlighter(LanguageNames.VisualBasic)>
    Friend Class XmlDeclarationHighlighter
        Inherits AbstractKeywordHighlighter(Of XmlDeclarationSyntax)

        <ImportingConstructor>
        Public Sub New()
        End Sub

        Protected Overloads Overrides Sub addHighlights(xmlDocumentPrologue As XmlDeclarationSyntax, highlights As List(Of TextSpan), cancellationToken As CancellationToken)
            With xmlDocumentPrologue
                If Not .ContainsDiagnostics AndAlso
                   Not .HasAncestor(Of DocumentationCommentTriviaSyntax)() Then
                    highlights.Add(.LessThanQuestionToken.Span)
                    highlights.Add(.QuestionGreaterThanToken.Span)
                End If
            End With
        End Sub
    End Class
End Namespace
