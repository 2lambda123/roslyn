﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports Microsoft.CodeAnalysis.Structure
Imports Microsoft.CodeAnalysis.VisualBasic.Structure
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.UnitTests.Outlining.MetadataAsSource
    <Trait(Traits.Feature, Traits.Features.MetadataAsSource)>
    Public Class EnumMemberDeclarationStructureProviderTests
        Inherits AbstractVisualBasicSyntaxNodeStructureProviderTests(Of EnumMemberDeclarationSyntax)

        Protected Overrides ReadOnly Property WorkspaceKind As String
            Get
                Return CodeAnalysis.WorkspaceKind.MetadataAsSource
            End Get
        End Property

        Friend Overrides Function CreateProvider() As AbstractSyntaxStructureProvider
            Return New EnumMemberDeclarationStructureProvider()
        End Function

        <Fact>
        Public Async Function NoCommentsOrAttributes() As Task
            Dim code = "
Enum E
    $$Goo
    Bar
End Enum
"

            Await VerifyNoBlockSpansAsync(code)
        End Function

        <Fact>
        Public Async Function WithAttributes() As Task
            Dim code = "
Enum E
    {|hint:{|textspan:<Blah>
    |}$$Goo|}
    Bar
End Enum
"

            Await VerifyBlockSpansAsync(code,
                Region("textspan", "hint", VisualBasicOutliningHelpers.Ellipsis, autoCollapse:=True))
        End Function

        <Fact>
        Public Async Function WithCommentsAndAttributes() As Task
            Dim code = "
Enum E
    {|hint:{|textspan:' Summary:
    '     This is a summary.
    <Blah>
    |}$$Goo|}
    Bar
End Enum
"

            Await VerifyBlockSpansAsync(code,
                Region("textspan", "hint", VisualBasicOutliningHelpers.Ellipsis, autoCollapse:=True))
        End Function
    End Class
End Namespace
