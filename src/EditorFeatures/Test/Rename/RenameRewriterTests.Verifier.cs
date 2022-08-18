﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Rename.ConflictEngine;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;
using RenameAnnotation = Microsoft.CodeAnalysis.Rename.ConflictEngine.RenameAnnotation;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.Rename
{
    public partial class RenameRewriterTests
    {
        private const string ConflictTag = "Conflict";
        private const string RenameTag = "Rename";

        protected sealed class Verifier : IDisposable
        {
            private readonly TestWorkspace _testWorkspace;
            private readonly RenamedSpansTracker _renamedSpansTracker = new();
            private Solution _currentSolution;

            public Verifier(string workspaceXml)
            {
                _testWorkspace = TestWorkspace.Create(workspaceXml);
                _currentSolution = _testWorkspace.CurrentSolution;
            }

            public async Task RenameAndAnnotatedDocumentAsync(
                string documentFilePath,
                Dictionary<string, (string replacementText, SymbolRenameOptions renameOptions)> renameTagsToReplacementInfo)
            {
                var testHostDocument = _testWorkspace.Documents.Single(doc => doc.FilePath == documentFilePath);
                var newRoot = await RenameDocumentAsync(_currentSolution, testHostDocument, renameTagsToReplacementInfo, CancellationToken.None).ConfigureAwait(false);
                if (newRoot == null)
                {
                    return;
                }

                var documentId = testHostDocument.Id;
                _currentSolution = _currentSolution.WithDocumentSyntaxRoot(documentId, newRoot);
            }

            public async Task VerifyAsync(
                string documentFilePath,
                string tagName,
                string replacementText)
            {
                var cancellationToken = CancellationToken.None;
                var testHostDocument = _testWorkspace.Documents.Single(doc => doc.FilePath == documentFilePath);
                var newDocument = _currentSolution.GetRequiredDocument(testHostDocument.Id);
                var sourceText = await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);

                foreach (var (tag, spans) in testHostDocument.AnnotatedSpans)
                {
                    if (tag == tagName)
                    {
                        foreach (var oldSpan in spans)
                        {
                            var newStartPosition = _renamedSpansTracker.GetAdjustedPosition(oldSpan.Start, testHostDocument.Id);
                            var newSpan = new TextSpan(newStartPosition, replacementText.Length);
                            var contentAtNewSpan = sourceText.ToString(newSpan);
                            Assert.Equal(replacementText, contentAtNewSpan);
                        }
                    }
                }
            }

            public async Task VerifyDocumentAsync(
                string documentFilePath,
                string expectedDocumentContent)
            {
                var documentId = _testWorkspace.Documents.Single(doc => doc.FilePath == documentFilePath).Id;
                var sourceText = await _currentSolution.GetRequiredDocument(documentId).GetTextAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.Equal(expectedDocumentContent.Trim(), sourceText.ToString().Trim());
            }

            private async Task<SyntaxNode?> RenameDocumentAsync(
                Solution solution,
                TestHostDocument testHostDocument,
                Dictionary<string, (string replacementText, SymbolRenameOptions renameOptions)> renameTagsToReplacementInfo,
                CancellationToken cancellationToken)
            {
                var document = solution.GetRequiredDocument(testHostDocument.Id);
                var renameRewriterService = document.GetRequiredLanguageService<IRenameRewriterLanguageService>();
                var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
                var annotatedSpans = testHostDocument.AnnotatedSpans;

                using var _1 = PooledHashSet<TextSpan>.GetInstance(out var conflictLocationSetBuilder);
                using var documentRenameBuilder = new DocumentRenameInfo.Builder();

                foreach (var (tag, spans) in annotatedSpans)
                {
                    if (tag == ConflictTag)
                    {
                        conflictLocationSetBuilder.AddRange(spans);
                    }
                    else if (tag.StartsWith(RenameTag))
                    {
                        foreach (var span in spans)
                        {
                            var renameSymbol = await RenameUtilities.TryGetRenamableSymbolAsync(document, span.Start, cancellationToken).ConfigureAwait(false);
                            if (renameSymbol == null)
                            {
                                AssertEx.Fail($"Can't find symbol at tagged place, tag: {tag}.");
                                return null;
                            }

                            if (!renameTagsToReplacementInfo.TryGetValue(tag, out var replacementInfo))
                            {
                                AssertEx.Fail($"Can't find the replacementInfo for tag: {tag}.");
                                return null;
                            }

                            var (replacementText, options) = replacementInfo;
                            var replacementTextValid = renameRewriterService.IsIdentifierValid(replacementText, syntaxFacts);
                            var possibleNameConflicts = new List<string>();

                            renameRewriterService.TryAddPossibleNameConflicts(renameSymbol, replacementText, possibleNameConflicts);
                            documentRenameBuilder.AddRenamedSymbol(renameSymbol, replacementText, replacementTextValid, possibleNameConflicts.ToImmutableArray());

                            var renameLocationsSet = await Renamer.FindRenameLocationsAsync(
                                solution,
                                renameSymbol,
                                options,
                                cancellationToken).ConfigureAwait(false);

                            var locationsInDocument = renameLocationsSet.Locations.Where(location => location.DocumentId == document.Id).ToImmutableArray();
                            var locationRenameContexts = locationsInDocument
                                .WhereAsArray(location => RenameUtilities.ShouldIncludeLocation(renameLocationsSet.Locations, location))
                                .SelectAsArray(location => new LocationRenameContext(location, replacementTextValid, replacementText, renameSymbol.Name));

                            foreach (var locationRenameContext in locationRenameContexts)
                            {
                                documentRenameBuilder.AddLocationRenameContext(locationRenameContext);
                            }

                            var stringAndCommentsRenameContexts = locationsInDocument
                                .WhereAsArray(location => location.IsRenameInStringOrComment)
                                .SelectAsArray(location => new StringAndCommentRenameContext(location, replacementText));
                            foreach (var stringAndCommentRenameContext in stringAndCommentsRenameContexts)
                            {
                                documentRenameBuilder.AddStringAndCommentRenameContext(stringAndCommentRenameContext);
                            }
                        }
                    }
                }

                var syntaxTree = await document.GetRequiredSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var parameters = new RenameRewriterParameters(
                    conflictLocationSetBuilder.ToImmutableHashSet(),
                    solution,
                    _renamedSpansTracker,
                    syntaxTree.GetRoot(cancellationToken),
                    document,
                    semanticModel,
                    new AnnotationTable<RenameAnnotation>(RenameAnnotation.Kind),
                    documentRenameBuilder.ToRenameInfo(),
                    cancellationToken);

                return renameRewriterService.AnnotateAndRename(parameters);
            }

            public void Dispose()
                => _testWorkspace.Dispose();
        }
    }
}
