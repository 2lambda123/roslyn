﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.CSharp.ConvertToRecord
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = PredefinedCodeFixProviderNames.ConvertToRecord), Shared]
    internal class CSharpConvertToRecordCodeFixProvider : CodeFixProvider
    {
        /// <summary>
        /// Only records may inherit from records.
        /// </summary>
        private const string CS8865 = nameof(CS8865);

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public CSharpConvertToRecordCodeFixProvider()
        {
        }

        public override FixAllProvider? GetFixAllProvider()
        {
            return null;
        }

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(CS8865);

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var document = context.Document;
            var span = context.Span;
            var cancellationToken = context.CancellationToken;

            if (!context.Options.GetOptions(document.Project.Services).EnableConvertToRecord)
            {
                return;
            }

            // get the class declaration. The span should be on the base type in the base list
            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var baseTypeSyntax = root.FindNode(span) as BaseTypeSyntax;

            var typeDeclaration = baseTypeSyntax?.GetAncestor<TypeDeclarationSyntax>();
            if (typeDeclaration == null)
            {
                return;
            }

            var action = await ConvertToRecordEngine.GetCodeActionAsync(
                document, typeDeclaration, cancellationToken).ConfigureAwait(false);

            if (action != null)
            {
                context.RegisterCodeFix(action, context.Diagnostics);
            }
        }
    }
}