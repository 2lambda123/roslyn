﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Collections.Immutable
Imports System.Threading
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.FindSymbols
Imports Microsoft.CodeAnalysis.LanguageServices
Imports Microsoft.CodeAnalysis.PooledObjects
Imports Microsoft.CodeAnalysis.Rename
Imports Microsoft.CodeAnalysis.Rename.ConflictEngine
Imports Microsoft.CodeAnalysis.Simplification
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Simplification
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax
Imports Microsoft.CodeAnalysis.VisualBasic.Utilities

Namespace Microsoft.CodeAnalysis.VisualBasic.Rename

    Friend Class VisualBasicRenameRewriterLanguageService
        Inherits AbstractRenameRewriterLanguageService

        Public Shared ReadOnly Instance As New VisualBasicRenameRewriterLanguageService()

        Private Sub New()
        End Sub

#Region "Annotation"

        Public Overrides Function AnnotateAndRename(parameters As RenameRewriterParameters) As SyntaxNode
            Dim renameRewriter = New SymbolsRenameRewriter(parameters)
            Return renameRewriter.Visit(parameters.SyntaxRoot)
        End Function

        Private NotInheritable Class SymbolsRenameRewriter
            Inherits VisualBasicSyntaxRewriter

            Private ReadOnly _documentId As DocumentId
            Private ReadOnly _solution As Solution
            Private ReadOnly _conflictLocations As ISet(Of TextSpan)
            Private ReadOnly _semanticModel As SemanticModel
            Private ReadOnly _cancellationToken As CancellationToken
            Private ReadOnly _renameSpansTracker As RenamedSpansTracker
            Private ReadOnly _simplificationService As ISimplificationService
            Private ReadOnly _annotatedIdentifierTokens As New HashSet(Of SyntaxToken)
            Private ReadOnly _invocationExpressionsNeedingConflictChecks As New HashSet(Of InvocationExpressionSyntax)
            Private ReadOnly _syntaxFactsService As ISyntaxFactsService
            Private ReadOnly _semanticFactsService As ISemanticFactsService
            Private ReadOnly _renameAnnotations As AnnotationTable(Of RenameAnnotation)

            ''' <summary>
            ''' Mapping from the span of renaming token to the renaming context info.
            ''' </summary>
            Private ReadOnly _textSpanToLocationContextMap As ImmutableDictionary(Of TextSpan, LocationRenameContext)

            ''' <summary>
            ''' Mapping from the symbolKey to all the possible symbols might be renamed in the document.
            ''' </summary>
            Private ReadOnly _stringAndCommentRenameContexts As ImmutableDictionary(Of TextSpan, ImmutableHashSet(Of LocationRenameContext))

            ''' <summary>
            ''' Mapping from the containgSpan of a common trivia/string identifier to a set of Locations needs to rename inside it.
            ''' It Is created by using a regex in to find the matched text when renaming inside a string/identifier.
            ''' </summary>
            Private ReadOnly _renamedSymbolContexts As ImmutableDictionary(Of SymbolKey, RenamedSymbolContext)

            Private ReadOnly Property AnnotateForComplexification As Boolean
                Get
                    Return Me._skipRenameForComplexification > 0 AndAlso Not Me._isProcessingComplexifiedSpans
                End Get
            End Property

            Private _skipRenameForComplexification As Integer
            Private _isProcessingComplexifiedSpans As Boolean
            Private _modifiedSubSpans As List(Of (TextSpan, TextSpan))
            Private _speculativeModel As SemanticModel

            Private ReadOnly _complexifiedSpans As HashSet(Of TextSpan) = New HashSet(Of TextSpan)

            Private Sub AddModifiedSpan(oldSpan As TextSpan, newSpan As TextSpan)
                newSpan = New TextSpan(oldSpan.Start, newSpan.Length)
                If Not Me._isProcessingComplexifiedSpans Then
                    _renameSpansTracker.AddModifiedSpan(_documentId, oldSpan, newSpan)
                Else
                    Me._modifiedSubSpans.Add((oldSpan, newSpan))
                End If
            End Sub

            Public Sub New(parameters As RenameRewriterParameters)
                MyBase.New(visitIntoStructuredTrivia:=True)
                Dim document = parameters.Document
                _documentId = document.Id
                _solution = parameters.OriginalSolution
                _conflictLocations = parameters.ConflictLocationSpans
                _cancellationToken = parameters.CancellationToken
                _semanticModel = parameters.SemanticModel
                _simplificationService = document.Project.Services.GetRequiredService(Of ISimplificationService)()
                _syntaxFactsService = document.Project.Services.GetRequiredService(Of ISyntaxFactsService)()
                _semanticFactsService = document.Project.Services.GetRequiredService(Of ISemanticFactsService)()
                _renameAnnotations = parameters.RenameAnnotations
                _renameSpansTracker = parameters.RenameSpansTracker

                ' TODO: These contexts are not changed for a document. ConflictResolver.Session should be refactored to cache them in a dictionary,
                _renamedSymbolContexts = CreateSymbolKeyToRenamedSymbolContextMap(parameters.RenameSymbolContexts, SymbolKey.GetComparer(ignoreCase:=True, ignoreAssemblyKeys:=False))
                _textSpanToLocationContextMap = CreateTextSpanToLocationContextMap(parameters.TokenTextSpanRenameContexts)
                _stringAndCommentRenameContexts = GroupStringAndCommentsTextSpanRenameContexts(parameters.StringAndCommentsTextSpanRenameContexts)
            End Sub

            Public Overrides Function Visit(node As SyntaxNode) As SyntaxNode
                If node Is Nothing Then
                    Return node
                End If

                Dim isInConflictLambdaBody = False
                Dim lambdas = node.GetAncestorsOrThis(Of MultiLineLambdaExpressionSyntax)()
                If lambdas.Count() <> 0 Then
                    For Each lambda In lambdas
                        If Me._conflictLocations.Any(Function(cf)
                                                         Return cf.Contains(lambda.Span)
                                                     End Function) Then
                            isInConflictLambdaBody = True
                            Exit For
                        End If
                    Next
                End If

                Dim shouldComplexifyNode = Me.ShouldComplexifyNode(node, isInConflictLambdaBody)

                Dim result As SyntaxNode
                If shouldComplexifyNode Then
                    Me._skipRenameForComplexification += 1
                    result = MyBase.Visit(node)
                    Me._skipRenameForComplexification -= 1
                    result = Complexify(node, result)
                Else
                    result = MyBase.Visit(node)
                End If

                Return result
            End Function

            Private Function ShouldComplexifyNode(node As SyntaxNode, isInConflictLambdaBody As Boolean) As Boolean
                Return Not isInConflictLambdaBody AndAlso
                       _skipRenameForComplexification = 0 AndAlso
                       Not _isProcessingComplexifiedSpans AndAlso
                       _conflictLocations.Contains(node.Span) AndAlso
                       (TypeOf node Is ExpressionSyntax OrElse
                        TypeOf node Is StatementSyntax OrElse
                        TypeOf node Is AttributeSyntax OrElse
                        TypeOf node Is SimpleArgumentSyntax OrElse
                        TypeOf node Is CrefReferenceSyntax OrElse
                        TypeOf node Is TypeConstraintSyntax)
            End Function

            Private Function Complexify(originalNode As SyntaxNode, newNode As SyntaxNode) As SyntaxNode
                If Me._complexifiedSpans.Contains(originalNode.Span) Then
                    Return newNode
                Else
                    Me._complexifiedSpans.Add(originalNode.Span)
                End If

                Me._isProcessingComplexifiedSpans = True
                Me._modifiedSubSpans = New List(Of ValueTuple(Of TextSpan, TextSpan))()
                Dim annotation = New SyntaxAnnotation()

                newNode = newNode.WithAdditionalAnnotations(annotation)
                Dim speculativeTree = originalNode.SyntaxTree.GetRoot(_cancellationToken).ReplaceNode(originalNode, newNode)
                newNode = speculativeTree.GetAnnotatedNodes(Of SyntaxNode)(annotation).First()
                Me._speculativeModel = GetSemanticModelForNode(newNode, Me._semanticModel)
                Debug.Assert(_speculativeModel IsNot Nothing, "expanding a syntax node which cannot be speculated?")

                ' There are cases when we change the type of node to make speculation work (e.g.,
                ' for AsNewClauseSyntax), so getting the newNode from the _speculativeModel 
                ' ensures the final node replacing the original node is found.
                newNode = Me._speculativeModel.SyntaxTree.GetRoot(_cancellationToken).GetAnnotatedNodes(Of SyntaxNode)(annotation).First()

                Dim oldSpan = originalNode.Span

                Dim expandParameter = originalNode.GetAncestorsOrThis(Of LambdaExpressionSyntax).Count() = 0

                Dim expandedNewNode = DirectCast(_simplificationService.Expand(newNode,
                                                                  _speculativeModel,
                                                                  annotationForReplacedAliasIdentifier:=Nothing,
                                                                  expandInsideNode:=AddressOf IsExpandWithinMultiLineLambda,
                                                                  expandParameter:=expandParameter,
                                                                  cancellationToken:=_cancellationToken), SyntaxNode)
                Dim annotationForSpeculativeNode = New SyntaxAnnotation()
                expandedNewNode = expandedNewNode.WithAdditionalAnnotations(annotationForSpeculativeNode)
                speculativeTree = originalNode.SyntaxTree.GetRoot(_cancellationToken).ReplaceNode(originalNode, expandedNewNode)
                Dim probableRenameNode = speculativeTree.GetAnnotatedNodes(Of SyntaxNode)(annotation).First()
                Dim speculativeNewNode = speculativeTree.GetAnnotatedNodes(Of SyntaxNode)(annotationForSpeculativeNode).First()

                Me._speculativeModel = GetSemanticModelForNode(speculativeNewNode, Me._semanticModel)
                Debug.Assert(_speculativeModel IsNot Nothing, "expanding a syntax node which cannot be speculated?")

                ' There are cases when we change the type of node to make speculation work (e.g.,
                ' for AsNewClauseSyntax), so getting the newNode from the _speculativeModel 
                ' ensures the final node replacing the original node is found.
                probableRenameNode = Me._speculativeModel.SyntaxTree.GetRoot(_cancellationToken).GetAnnotatedNodes(Of SyntaxNode)(annotation).First()
                speculativeNewNode = Me._speculativeModel.SyntaxTree.GetRoot(_cancellationToken).GetAnnotatedNodes(Of SyntaxNode)(annotationForSpeculativeNode).First()

                Dim renamedNode = MyBase.Visit(probableRenameNode)

                If Not ReferenceEquals(renamedNode, probableRenameNode) Then
                    renamedNode = renamedNode.WithoutAnnotations(annotation)
                    probableRenameNode = expandedNewNode.GetAnnotatedNodes(Of SyntaxNode)(annotation).First()
                    expandedNewNode = expandedNewNode.ReplaceNode(probableRenameNode, renamedNode)
                End If

                Dim newSpan = expandedNewNode.Span
                probableRenameNode = probableRenameNode.WithoutAnnotations(annotation)
                expandedNewNode = Me._renameAnnotations.WithAdditionalAnnotations(expandedNewNode, New RenameNodeSimplificationAnnotation() With {.OriginalTextSpan = oldSpan})

                Me._renameSpansTracker.AddComplexifiedSpan(Me._documentId, oldSpan, New TextSpan(oldSpan.Start, newSpan.Length), Me._modifiedSubSpans)
                Me._modifiedSubSpans = Nothing
                Me._isProcessingComplexifiedSpans = False
                Me._speculativeModel = Nothing
                Return expandedNewNode
            End Function

            Private Function IsExpandWithinMultiLineLambda(node As SyntaxNode) As Boolean
                If node Is Nothing Then
                    Return False
                End If

                If Me._conflictLocations.Contains(node.Span) Then
                    Return True
                End If

                If node.IsParentKind(SyntaxKind.MultiLineSubLambdaExpression) OrElse
                node.IsParentKind(SyntaxKind.MultiLineFunctionLambdaExpression) Then
                    Dim parent = DirectCast(node.Parent, MultiLineLambdaExpressionSyntax)
                    If ReferenceEquals(parent.SubOrFunctionHeader, node) Then
                        Return True
                    Else
                        Return False
                    End If
                End If

                Return True
            End Function

            Private Shared Function IsPossibleNameConflict(possibleNameConflicts As ICollection(Of String), candidate As String) As Boolean
                For Each possibleNameConflict In possibleNameConflicts
                    If CaseInsensitiveComparison.Equals(possibleNameConflict, candidate) Then
                        Return True
                    End If
                Next

                Return False
            End Function

            Private Function UpdateAliasAnnotation(newToken As SyntaxToken) As SyntaxToken

                For Each kvp In _renamedSymbolContexts
                    Dim renameSymbolContext = kvp.Value
                    Dim aliasSymbol = renameSymbolContext.AliasSymbol
                    If aliasSymbol IsNot Nothing AndAlso Not Me.AnnotateForComplexification AndAlso newToken.HasAnnotations(AliasAnnotation.Kind) Then
                        newToken = RenameUtilities.UpdateAliasAnnotation(newToken, aliasSymbol, renameSymbolContext.ReplacementText)
                    End If
                Next

                Return newToken
            End Function

            Private Async Function AnnotateForConflictCheckAsync(token As SyntaxToken, newToken As SyntaxToken, isOldText As Boolean) As Task(Of SyntaxToken)
                If token.IsKind(SyntaxKind.NewKeyword) Then
                    ' The constructor definition cannot be renamed in Visual Basic
                    Return newToken
                End If

                Dim isNamespaceDeclarationReference = token.GetPreviousToken().Kind = SyntaxKind.NamespaceKeyword
                Dim symbols = RenameUtilities.GetSymbolsTouchingPosition(token.Span.Start, _semanticModel, _solution.Services, _cancellationToken)
                If symbols.Length = 1 Then
                    If TypeOf symbols(0) Is INamespaceSymbol AndAlso isNamespaceDeclarationReference Then
                        Return newToken
                    End If
                End If

                Dim renameDeclarationLocations As RenameDeclarationLocationReference() =
                   Await ConflictResolver.CreateDeclarationLocationAnnotationsAsync(_solution, symbols, _cancellationToken).ConfigureAwait(False)

                Dim isMemberGroupReference = token.Parent IsNot Nothing AndAlso _semanticFactsService.IsInsideNameOfExpression(_semanticModel, token.Parent, _cancellationToken)

                Dim renameAnnotation = New RenameActionAnnotation(
                                    token.Span,
                                    isRenameLocation:=False,
                                    Nothing,
                                    Nothing,
                                    isOldText,
                                    renameDeclarationLocations,
                                    isNamespaceDeclarationReference:=isNamespaceDeclarationReference,
                                    isInvocationExpression:=False,
                                    isMemberGroupReference:=isMemberGroupReference)

                _annotatedIdentifierTokens.Add(token)
                _invocationExpressionsNeedingConflictChecks.AddRange(token.GetAncestors(Of InvocationExpressionSyntax)())
                newToken = Me._renameAnnotations.WithAdditionalAnnotations(newToken, renameAnnotation, New RenameTokenSimplificationAnnotation() With {.OriginalTextSpan = token.Span})
                Return newToken
            End Function

            Private Async Function RenameAndAnnotateAsync(
                token As SyntaxToken,
                newToken As SyntaxToken,
                isVerbatim As Boolean,
                replacementTextValid As Boolean,
                isRenamableAccessor As Boolean,
                originalText As String,
                replacementText As String) As Task(Of SyntaxToken)

                If newToken.IsKind(SyntaxKind.NewKeyword) Then
                    ' The constructor definition cannot be renamed in Visual Basic
                    Return newToken
                End If

                If Me._isProcessingComplexifiedSpans Then
                    Dim annotation = Me._renameAnnotations.GetAnnotations(Of RenameActionAnnotation)(token).FirstOrDefault()
                    If annotation IsNot Nothing Then
                        newToken = RenameToken(token, newToken, annotation.Prefix, annotation.Suffix, isVerbatim, originalText, replacementText, replacementTextValid)
                        AddModifiedSpan(annotation.OriginalSpan, New TextSpan(token.Span.Start, newToken.Span.Length))
                    Else
                        newToken = RenameToken(token, newToken, prefix:=Nothing, suffix:=Nothing, isVerbatim, replacementText, originalText, replacementTextValid)
                    End If

                    Return newToken
                End If

                Dim symbols = RenameUtilities.GetSymbolsTouchingPosition(token.Span.Start, _semanticModel, _solution.Services, _cancellationToken)

                ' this is the compiler generated backing field of a non custom event. We need to store a "Event" suffix to properly rename it later on.
                Dim prefix = If(isRenamableAccessor, newToken.ValueText.Substring(0, newToken.ValueText.IndexOf("_"c) + 1), String.Empty)
                Dim suffix As String = Nothing

                If symbols.Length = 1 Then
                    Dim symbol = symbols(0)

                    If symbol.IsConstructor() Then
                        symbol = symbol.ContainingSymbol
                    End If

                    If symbol.Kind = SymbolKind.Field AndAlso symbol.IsImplicitlyDeclared Then
                        Dim fieldSymbol = DirectCast(symbol, IFieldSymbol)

                        If fieldSymbol.Type.IsDelegateType AndAlso
                    fieldSymbol.Type.IsImplicitlyDeclared AndAlso
                    DirectCast(fieldSymbol.Type, INamedTypeSymbol).AssociatedSymbol IsNot Nothing Then

                            suffix = "Event"
                        End If

                        If fieldSymbol.AssociatedSymbol IsNot Nothing AndAlso
                       fieldSymbol.AssociatedSymbol.IsKind(SymbolKind.Property) AndAlso
                       fieldSymbol.Name = "_" + fieldSymbol.AssociatedSymbol.Name Then

                            prefix = "_"
                        End If

                    ElseIf symbol.IsConstructor AndAlso
                     symbol.ContainingType.IsImplicitlyDeclared AndAlso
                     symbol.ContainingType.IsDelegateType AndAlso
                     symbol.ContainingType.AssociatedSymbol IsNot Nothing Then

                        suffix = "EventHandler"
                    ElseIf TypeOf symbol Is INamedTypeSymbol Then
                        Dim namedTypeSymbol = DirectCast(symbol, INamedTypeSymbol)
                        If namedTypeSymbol.IsImplicitlyDeclared AndAlso
                            namedTypeSymbol.IsDelegateType() AndAlso
                            namedTypeSymbol.AssociatedSymbol IsNot Nothing Then
                            suffix = "EventHandler"
                        End If
                    End If
                End If

                If Not Me.AnnotateForComplexification Then
                    Dim oldSpan = token.Span
                    newToken = RenameToken(token, newToken, prefix:=prefix, suffix:=suffix, isVerbatim, originalText, replacementText, replacementTextValid)
                    AddModifiedSpan(oldSpan, newToken.Span)
                End If

                Dim renameDeclarationLocations As RenameDeclarationLocationReference() =
               Await ConflictResolver.CreateDeclarationLocationAnnotationsAsync(_solution, symbols, _cancellationToken).ConfigureAwait(False)

                Dim isNamespaceDeclarationReference = token.GetPreviousToken().Kind = SyntaxKind.NamespaceKeyword

                Dim isMemberGroupReference = _semanticFactsService.IsInsideNameOfExpression(_semanticModel, token.Parent, _cancellationToken)

                Dim renameAnnotation = New RenameActionAnnotation(
                                token.Span,
                                isRenameLocation:=True,
                                prefix,
                                suffix,
                                isOriginalTextLocation:=token.ValueText = originalText,
                                renameDeclarationLocations,
                                isNamespaceDeclarationReference,
                                isInvocationExpression:=False,
                                isMemberGroupReference:=isMemberGroupReference)

                _annotatedIdentifierTokens.Add(token)
                newToken = Me._renameAnnotations.WithAdditionalAnnotations(newToken, renameAnnotation, New RenameTokenSimplificationAnnotation() With {.OriginalTextSpan = token.Span})

                Return newToken
            End Function

            Public Overrides Function VisitTrivia(trivia As SyntaxTrivia) As SyntaxTrivia
                Dim newTrivia = MyBase.VisitTrivia(trivia)

                Dim textSpanRenameContexts As ImmutableHashSet(Of LocationRenameContext) = Nothing
                If Not trivia.HasStructure AndAlso _stringAndCommentRenameContexts.TryGetValue(trivia.Span, textSpanRenameContexts) Then
                    Dim subSpanToReplacementText = CreateSubSpanToReplacementTextDictionary(textSpanRenameContexts)
                    Return RenameInCommentTrivia(newTrivia, subSpanToReplacementText)
                End If

                Return newTrivia
            End Function

            Public Overrides Function VisitToken(oldToken As SyntaxToken) As SyntaxToken
                If oldToken = Nothing Then
                    Return oldToken
                End If

                Dim newToken = MyBase.VisitToken(oldToken)
                newToken = UpdateAliasAnnotation(newToken)

                ' Rename matches in strings and comments
                newToken = RenameWithinToken(oldToken, newToken)

                ' We don't want to annotate XmlName with RenameActionAnnotation
                If newToken.Kind = SyntaxKind.XmlNameToken Then
                    Return newToken
                End If

                Dim locationRenameContext As LocationRenameContext = Nothing
                If Not _isProcessingComplexifiedSpans AndAlso _textSpanToLocationContextMap.TryGetValue(oldToken.Span, locationRenameContext) Then
                    newToken = RenameAndAnnotateAsync(
                        oldToken,
                        newToken,
                        isVerbatim:=_syntaxFactsService.IsVerbatimIdentifier(locationRenameContext.ReplacementText),
                        replacementTextValid:=locationRenameContext.ReplacementTextValid,
                        isRenamableAccessor:=locationRenameContext.RenameLocation.IsRenamableAccessor,
                        originalText:=locationRenameContext.OriginalText,
                        replacementText:=locationRenameContext.ReplacementText).WaitAndGetResult_CanCallOnBackground(_cancellationToken)
                    _invocationExpressionsNeedingConflictChecks.AddRange(oldToken.GetAncestors(Of InvocationExpressionSyntax)())
                    Return newToken
                End If

                If _isProcessingComplexifiedSpans Then
                    Return RenameTokenWhenProcessingComplexiedSpans(oldToken, newToken)
                End If

                Return AnnotateNonRenameLocation(oldToken, newToken)
            End Function

            Private Function RenameTokenWhenProcessingComplexiedSpans(token As SyntaxToken, newToken As SyntaxToken) As SyntaxToken
                If Not _isProcessingComplexifiedSpans Then
                    Return newToken
                End If

                RoslynDebug.Assert(_speculativeModel IsNot Nothing)

                If token.HasAnnotations(AliasAnnotation.Kind) Then
                    Return newToken
                End If

                If token.HasAnnotations(RenameAnnotation.Kind) Then
                    Dim annotation = _renameAnnotations.GetAnnotations(token).OfType(Of RenameActionAnnotation).First()

                    Dim originalContext As LocationRenameContext = Nothing
                    If annotation.IsRenameLocation AndAlso _textSpanToLocationContextMap.TryGetValue(annotation.OriginalSpan, originalContext) Then
                        Return RenameComplexifiedToken(token, newToken, originalContext)
                    Else
                        Return newToken
                    End If
                End If

                If TypeOf token.Parent Is SimpleNameSyntax AndAlso token.Kind <> SyntaxKind.GlobalKeyword AndAlso token.Parent.Parent.IsKind(SyntaxKind.QualifiedName, SyntaxKind.QualifiedCrefOperatorReference) Then
                    Dim symbol = Me._speculativeModel.GetSymbolInfo(token.Parent, Me._cancellationToken).Symbol
                    Dim renamedSymbolContext As RenamedSymbolContext = Nothing
                    If symbol IsNot Nothing AndAlso
                        _renamedSymbolContexts.TryGetValue(symbol.GetSymbolKey(), renamedSymbolContext) AndAlso
                        renamedSymbolContext.RenamedSymbol.Kind <> SymbolKind.Local AndAlso
                        renamedSymbolContext.RenamedSymbol.Kind <> SymbolKind.RangeVariable AndAlso
                        token.ValueText = renamedSymbolContext.OriginalText Then
                        Return RenameComplexifiedToken(token, newToken, renamedSymbolContext)
                    End If
                End If

                Return newToken
            End Function

            Private Function RenameComplexifiedToken(token As SyntaxToken, newToken As SyntaxToken, locationRenameContext As LocationRenameContext) As SyntaxToken
                If _isProcessingComplexifiedSpans Then
                    Dim annotation = _renameAnnotations.GetAnnotations(token).OfType(Of RenameActionAnnotation)().FirstOrDefault()

                    newToken = RenameToken(
                            token,
                            newToken,
                            annotation.Prefix,
                            annotation.Suffix,
                            _syntaxFactsService.IsVerbatimIdentifier(locationRenameContext.ReplacementText),
                            locationRenameContext.OriginalText,
                            locationRenameContext.ReplacementText,
                            locationRenameContext.ReplacementTextValid)

                    AddModifiedSpan(annotation.OriginalSpan, newToken.Span)
                End If

                Return newToken
            End Function

            Private Function RenameComplexifiedToken(token As SyntaxToken, newToken As SyntaxToken, renamedSymbolContext As RenamedSymbolContext) As SyntaxToken
                If _isProcessingComplexifiedSpans Then
                    Return RenameToken(
                            token,
                            newToken,
                            prefix:=Nothing,
                            suffix:=Nothing,
                            _syntaxFactsService.IsVerbatimIdentifier(renamedSymbolContext.ReplacementText),
                            renamedSymbolContext.OriginalText,
                            renamedSymbolContext.ReplacementText,
                            renamedSymbolContext.ReplacementTextValid)
                End If

                Return newToken
            End Function

            Private Function AnnotateNonRenameLocation(token As SyntaxToken, newToken As SyntaxToken) As SyntaxToken
                If Not _isProcessingComplexifiedSpans Then
                    Dim renameContexts = _renamedSymbolContexts.Values.ToSet()
                    Dim tokenText = token.ValueText
                    Dim isOldText = renameContexts.Any(Function(c) CaseInsensitiveComparison.Equals(tokenText, c.OriginalText))
                    Dim tokenNeedsConflictCheck = isOldText OrElse
                                                  renameContexts.Any(Function(c) CaseInsensitiveComparison.Equals(tokenText, c.ReplacementText) OrElse IsPossibleNameConflict(c.PossibleNameConflicts, tokenText))

                    If tokenNeedsConflictCheck Then
                        newToken = AnnotateForConflictCheckAsync(token, newToken, isOldText).WaitAndGetResult_CanCallOnBackground(_cancellationToken)
                    End If

                    Return newToken
                End If

                Return newToken
            End Function

            Private Function GetAnnotationForInvocationExpression(invocationExpression As InvocationExpressionSyntax) As RenameActionAnnotation
                Dim identifierToken As SyntaxToken = Nothing
                Dim expressionOfInvocation = invocationExpression.Expression
                While expressionOfInvocation IsNot Nothing
                    Select Case expressionOfInvocation.Kind
                        Case SyntaxKind.IdentifierName, SyntaxKind.GenericName
                            identifierToken = DirectCast(expressionOfInvocation, SimpleNameSyntax).Identifier
                            Exit While
                        Case SyntaxKind.SimpleMemberAccessExpression
                            identifierToken = DirectCast(expressionOfInvocation, MemberAccessExpressionSyntax).Name.Identifier
                            Exit While
                        Case SyntaxKind.QualifiedName
                            identifierToken = DirectCast(expressionOfInvocation, QualifiedNameSyntax).Right.Identifier
                            Exit While
                        Case SyntaxKind.ParenthesizedExpression
                            expressionOfInvocation = DirectCast(expressionOfInvocation, ParenthesizedExpressionSyntax).Expression
                        Case SyntaxKind.MeExpression
                            Exit While
                        Case Else
                            ' This isn't actually an invocation, so there's no member name to check.
                            Return Nothing
                    End Select
                End While

                If identifierToken <> Nothing AndAlso Not Me._annotatedIdentifierTokens.Contains(identifierToken) Then
                    Dim symbolInfo = Me._semanticModel.GetSymbolInfo(invocationExpression, Me._cancellationToken)
                    Dim symbols As IEnumerable(Of ISymbol)
                    If symbolInfo.Symbol Is Nothing Then
                        Return Nothing
                    Else
                        symbols = SpecializedCollections.SingletonEnumerable(symbolInfo.Symbol)
                    End If

                    Dim renameDeclarationLocations As RenameDeclarationLocationReference() =
                        ConflictResolver.CreateDeclarationLocationAnnotationsAsync(_solution, symbols, _cancellationToken).WaitAndGetResult_CanCallOnBackground(_cancellationToken)

                    Dim renameAnnotation = New RenameActionAnnotation(
                                            identifierToken.Span,
                                            isRenameLocation:=False,
                                            prefix:=Nothing,
                                            suffix:=Nothing,
                                            renameDeclarationLocations:=renameDeclarationLocations,
                                            isOriginalTextLocation:=False,
                                            isNamespaceDeclarationReference:=False,
                                            isInvocationExpression:=True,
                                            isMemberGroupReference:=False)

                    Return renameAnnotation
                End If

                Return Nothing
            End Function

            Public Overrides Function VisitInvocationExpression(node As InvocationExpressionSyntax) As SyntaxNode
                Dim result = MyBase.VisitInvocationExpression(node)
                If _invocationExpressionsNeedingConflictChecks.Contains(node) Then
                    Dim renameAnnotation = GetAnnotationForInvocationExpression(node)
                    If renameAnnotation IsNot Nothing Then
                        result = Me._renameAnnotations.WithAdditionalAnnotations(result, renameAnnotation)
                    End If
                End If

                Return result
            End Function

            Private Function RenameToken(
                    oldToken As SyntaxToken,
                    newToken As SyntaxToken,
                    prefix As String,
                    suffix As String,
                    isReplacementTextVerbatim As Boolean,
                    originalText As String,
                    replacementText As String,
                    isReplacementTextValid As Boolean) As SyntaxToken

                Dim parent = oldToken.Parent
                Dim currentNewIdentifier = replacementText
                Dim oldIdentifier = newToken.ValueText
                Dim isAttributeName = SyntaxFacts.IsAttributeName(parent)
                If isAttributeName Then
                    If oldIdentifier <> originalText Then
                        Dim withoutSuffix = String.Empty
                        If currentNewIdentifier.TryReduceAttributeSuffix(withoutSuffix) Then
                            currentNewIdentifier = withoutSuffix
                        End If
                    End If
                Else
                    If Not String.IsNullOrEmpty(prefix) Then
                        currentNewIdentifier = prefix + currentNewIdentifier
                    End If

                    If Not String.IsNullOrEmpty(suffix) Then
                        currentNewIdentifier = currentNewIdentifier + suffix
                    End If
                End If

                ' determine the canonical identifier name (unescaped, no type char, ...)
                Dim valueText = currentNewIdentifier
                Dim name = SyntaxFactory.ParseName(currentNewIdentifier)
                If name.ContainsDiagnostics Then
                    name = SyntaxFactory.IdentifierName(currentNewIdentifier)
                End If

                If name.IsKind(SyntaxKind.GlobalName) Then
                    valueText = currentNewIdentifier
                ElseIf name.IsKind(SyntaxKind.IdentifierName) Then
                    valueText = DirectCast(name, IdentifierNameSyntax).Identifier.ValueText
                End If

                If isReplacementTextVerbatim Then
                    newToken = newToken.CopyAnnotationsTo(SyntaxFactory.BracketedIdentifier(newToken.LeadingTrivia, valueText, newToken.TrailingTrivia))
                Else
                    newToken = newToken.CopyAnnotationsTo(SyntaxFactory.Identifier(
                                                          newToken.LeadingTrivia,
                                                          If(oldToken.GetTypeCharacter() = TypeCharacter.None, currentNewIdentifier, currentNewIdentifier + oldToken.ToString().Last()),
                                                          False,
                                                          valueText,
                                                      oldToken.GetTypeCharacter(),
                                                          newToken.TrailingTrivia))

                    If isReplacementTextValid AndAlso
                        oldToken.GetTypeCharacter() <> TypeCharacter.None AndAlso
                        (SyntaxFacts.GetKeywordKind(valueText) = SyntaxKind.REMKeyword OrElse Me._syntaxFactsService.IsVerbatimIdentifier(newToken)) Then

                        newToken = Me._renameAnnotations.WithAdditionalAnnotations(newToken, RenameInvalidIdentifierAnnotation.Instance)
                    End If
                End If

                If isReplacementTextValid Then
                    If newToken.IsBracketed Then
                        ' a reference location should always be tried to be unescaped, whether it was escaped before rename 
                        ' or the replacement itself is escaped.
                        newToken = newToken.WithAdditionalAnnotations(Simplifier.Annotation)
                    Else
                        newToken = TryEscapeIdentifierToken(newToken)
                    End If
                End If

                Return newToken
            End Function

            Private Function RenameInStringLiteral(oldToken As SyntaxToken, newToken As SyntaxToken, subSpanToReplacementText As ImmutableSortedDictionary(Of TextSpan, String), createNewStringLiteral As Func(Of SyntaxTriviaList, String, String, SyntaxTriviaList, SyntaxToken)) As SyntaxToken
                Dim originalString = newToken.ToString()
                Dim replacedString = RenameUtilities.ReplaceMatchingSubStrings(originalString, subSpanToReplacementText)
                If replacedString <> originalString Then
                    Dim oldSpan = oldToken.Span
                    newToken = createNewStringLiteral(newToken.LeadingTrivia, replacedString, replacedString, newToken.TrailingTrivia)
                    AddModifiedSpan(oldSpan, newToken.Span)
                    Return oldToken.CopyAnnotationsTo(Me._renameAnnotations.WithAdditionalAnnotations(newToken, New RenameTokenSimplificationAnnotation() With {.OriginalTextSpan = oldSpan}))
                End If

                Return newToken
            End Function

            Private Function RenameInCommentTrivia(trivia As SyntaxTrivia, subSpanToReplacementText As ImmutableSortedDictionary(Of TextSpan, String)) As SyntaxTrivia
                Dim originalString = trivia.ToString()
                Dim replacedString As String = RenameUtilities.ReplaceMatchingSubStrings(originalString, subSpanToReplacementText)
                If replacedString <> originalString Then
                    Dim oldSpan = trivia.Span
                    Dim newTrivia = SyntaxFactory.CommentTrivia(replacedString)
                    AddModifiedSpan(oldSpan, newTrivia.Span)
                    Return trivia.CopyAnnotationsTo(Me._renameAnnotations.WithAdditionalAnnotations(newTrivia, New RenameTokenSimplificationAnnotation() With {.OriginalTextSpan = oldSpan}))
                End If

                Return trivia
            End Function

            Private Function RenameWithinToken(token As SyntaxToken, newToken As SyntaxToken) As SyntaxToken
                Dim locationSymbolContexts As ImmutableHashSet(Of LocationRenameContext) = Nothing
                If _isProcessingComplexifiedSpans OrElse Not _stringAndCommentRenameContexts.TryGetValue(token.Span, locationSymbolContexts) OrElse locationSymbolContexts.Count = 0 Then
                    Return newToken
                End If

                Dim subSpanToReplacementText = CreateSubSpanToReplacementTextDictionary(locationSymbolContexts)

                Dim kind = newToken.Kind()
                If kind = SyntaxKind.StringLiteralToken Then
                    newToken = RenameInStringLiteral(token, newToken, subSpanToReplacementText, AddressOf SyntaxFactory.StringLiteralToken)
                ElseIf kind = SyntaxKind.InterpolatedStringTextToken Then
                    newToken = RenameInStringLiteral(token, newToken, subSpanToReplacementText, AddressOf SyntaxFactory.InterpolatedStringTextToken)
                ElseIf kind = SyntaxKind.XmlTextLiteralToken Then
                    newToken = RenameInStringLiteral(token, newToken, subSpanToReplacementText, AddressOf SyntaxFactory.XmlTextLiteralToken)
                ElseIf kind = SyntaxKind.XmlNameToken Then
                    Dim originalText = newToken.ToString()
                    Dim replacementText = RenameUtilities.ReplaceMatchingSubStrings(originalText, subSpanToReplacementText)
                    If replacementText <> originalText Then
                        Dim newIdentifierToken = SyntaxFactory.XmlNameToken(newToken.LeadingTrivia, replacementText, SyntaxFacts.GetKeywordKind(replacementText), newToken.TrailingTrivia)
                        newToken = token.CopyAnnotationsTo(Me._renameAnnotations.WithAdditionalAnnotations(newIdentifierToken, New RenameTokenSimplificationAnnotation() With {.OriginalTextSpan = token.Span}))
                        AddModifiedSpan(token.Span, newToken.Span)
                    End If
                End If

                Return newToken
            End Function
        End Class
#End Region

#Region "Declaration Conflicts"

        Public Overrides Function LocalVariableConflict(
            token As SyntaxToken,
            newReferencedSymbols As IEnumerable(Of ISymbol)) As Boolean

            ' This scenario is not present in VB and only in C#
            Return False
        End Function

        Public Overrides Function ComputeDeclarationConflictsAsync(
            replacementText As String,
            renamedSymbol As ISymbol,
            renameSymbol As ISymbol,
            referencedSymbols As IEnumerable(Of ISymbol),
            baseSolution As Solution,
            newSolution As Solution,
            reverseMappedLocations As IDictionary(Of Location, Location),
            cancellationToken As CancellationToken
        ) As Task(Of ImmutableArray(Of Location))

            Dim conflicts = ArrayBuilder(Of Location).GetInstance()

            If renamedSymbol.Kind = SymbolKind.Parameter OrElse
               renamedSymbol.Kind = SymbolKind.Local OrElse
               renamedSymbol.Kind = SymbolKind.RangeVariable Then

                Dim token = renamedSymbol.Locations.Single().FindToken(cancellationToken)

                ' Find the method block or field declaration that we're in. Note the LastOrDefault
                ' so we find the uppermost one, since VariableDeclarators live in methods too.
                Dim methodBase = token.Parent.AncestorsAndSelf.Where(Function(s) TypeOf s Is MethodBlockBaseSyntax OrElse TypeOf s Is VariableDeclaratorSyntax) _
                                                              .LastOrDefault()

                Dim visitor As New LocalConflictVisitor(token, newSolution, cancellationToken)
                visitor.Visit(methodBase)

                conflicts.AddRange(visitor.ConflictingTokens.Select(Function(t) t.GetLocation()) _
                               .Select(Function(loc) reverseMappedLocations(loc)))

                ' If this is a parameter symbol for a partial method definition, be sure we visited 
                ' the implementation part's body.
                If renamedSymbol.Kind = SymbolKind.Parameter AndAlso
                    renamedSymbol.ContainingSymbol.Kind = SymbolKind.Method Then
                    Dim methodSymbol = DirectCast(renamedSymbol.ContainingSymbol, IMethodSymbol)
                    If methodSymbol.PartialImplementationPart IsNot Nothing Then
                        Dim matchingParameterSymbol = methodSymbol.PartialImplementationPart.Parameters((DirectCast(renamedSymbol, IParameterSymbol)).Ordinal)

                        token = matchingParameterSymbol.Locations.Single().FindToken(cancellationToken)
                        methodBase = token.GetAncestor(Of MethodBlockSyntax)
                        visitor = New LocalConflictVisitor(token, newSolution, cancellationToken)
                        visitor.Visit(methodBase)

                        conflicts.AddRange(visitor.ConflictingTokens.Select(Function(t) t.GetLocation()) _
                                       .Select(Function(loc) reverseMappedLocations(loc)))
                    End If
                End If

                ' in VB parameters of properties are not allowed to be the same as the containing property
                If renamedSymbol.Kind = SymbolKind.Parameter AndAlso
                    renamedSymbol.ContainingSymbol.Kind = SymbolKind.Property AndAlso
                    CaseInsensitiveComparison.Equals(renamedSymbol.ContainingSymbol.Name, renamedSymbol.Name) Then

                    Dim propertySymbol = renamedSymbol.ContainingSymbol

                    While propertySymbol IsNot Nothing
                        conflicts.AddRange(renamedSymbol.ContainingSymbol.Locations _
                                       .Select(Function(loc) reverseMappedLocations(loc)))

                        propertySymbol = propertySymbol.GetOverriddenMember()
                    End While
                End If

            ElseIf renamedSymbol.Kind = SymbolKind.Label Then
                Dim token = renamedSymbol.Locations.Single().FindToken(cancellationToken)
                Dim containingMethod = token.Parent.FirstAncestorOrSelf(Of SyntaxNode)(
                    Function(s) TypeOf s Is MethodBlockBaseSyntax OrElse
                                TypeOf s Is LambdaExpressionSyntax)

                Dim visitor As New LabelConflictVisitor(token)
                visitor.Visit(containingMethod)
                conflicts.AddRange(visitor.ConflictingTokens.Select(Function(t) t.GetLocation()) _
                    .Select(Function(loc) reverseMappedLocations(loc)))

            ElseIf renamedSymbol.Kind = SymbolKind.Method Then
                conflicts.AddRange(
                    DeclarationConflictHelpers.GetMembersWithConflictingSignatures(DirectCast(renamedSymbol, IMethodSymbol), trimOptionalParameters:=True) _
                        .Select(Function(loc) reverseMappedLocations(loc)))

            ElseIf renamedSymbol.Kind = SymbolKind.Property Then
                conflicts.AddRange(
                    DeclarationConflictHelpers.GetMembersWithConflictingSignatures(DirectCast(renamedSymbol, IPropertySymbol), trimOptionalParameters:=True) _
                        .Select(Function(loc) reverseMappedLocations(loc)))
                AddConflictingParametersOfProperties(
                    referencedSymbols.Concat(renameSymbol).Where(Function(sym) sym.Kind = SymbolKind.Property),
                    renamedSymbol.Name,
                    conflicts)

            ElseIf renamedSymbol.Kind = SymbolKind.TypeParameter Then
                For Each location In renamedSymbol.Locations
                    Dim token = location.FindToken(cancellationToken)
                    Dim currentTypeParameter = token.Parent

                    For Each typeParameter In DirectCast(currentTypeParameter.Parent, TypeParameterListSyntax).Parameters
                        If typeParameter IsNot currentTypeParameter AndAlso CaseInsensitiveComparison.Equals(token.ValueText, typeParameter.Identifier.ValueText) Then
                            conflicts.Add(reverseMappedLocations(typeParameter.Identifier.GetLocation()))
                        End If
                    Next
                Next
            End If

            ' if the renamed symbol is a type member, it's name should not conflict with a type parameter
            If renamedSymbol.ContainingType IsNot Nothing AndAlso renamedSymbol.ContainingType.GetMembers(renamedSymbol.Name).Contains(renamedSymbol) Then
                Dim conflictingLocations = renamedSymbol.ContainingType.TypeParameters _
                    .Where(Function(t) CaseInsensitiveComparison.Equals(t.Name, renamedSymbol.Name)) _
                    .SelectMany(Function(t) t.Locations)

                For Each location In conflictingLocations
                    Dim typeParameterToken = location.FindToken(cancellationToken)
                    conflicts.Add(reverseMappedLocations(typeParameterToken.GetLocation()))
                Next
            End If

            Return Task.FromResult(conflicts.ToImmutableAndFree())
        End Function

        Public Overrides Async Function ComputeImplicitReferenceConflictsAsync(
                renameSymbol As ISymbol, renamedSymbol As ISymbol,
                implicitReferenceLocations As IEnumerable(Of ReferenceLocation),
                cancellationToken As CancellationToken) As Task(Of ImmutableArray(Of Location))

            ' Handle renaming of symbols used for foreach
            Dim implicitReferencesMightConflict = renameSymbol.Kind = SymbolKind.Property AndAlso
                                                CaseInsensitiveComparison.Equals(renameSymbol.Name, "Current")
            implicitReferencesMightConflict = implicitReferencesMightConflict OrElse
                                                (renameSymbol.Kind = SymbolKind.Method AndAlso
                                                    (CaseInsensitiveComparison.Equals(renameSymbol.Name, "MoveNext") OrElse
                                                    CaseInsensitiveComparison.Equals(renameSymbol.Name, "GetEnumerator")))

            ' TODO: handle Dispose for using statement and Add methods for collection initializers.

            If implicitReferencesMightConflict Then
                If Not CaseInsensitiveComparison.Equals(renamedSymbol.Name, renameSymbol.Name) Then
                    For Each implicitReferenceLocation In implicitReferenceLocations
                        Dim token = Await implicitReferenceLocation.Location.SourceTree.GetTouchingTokenAsync(
                            implicitReferenceLocation.Location.SourceSpan.Start, cancellationToken, findInsideTrivia:=False).ConfigureAwait(False)

                        If token.Kind = SyntaxKind.ForKeyword AndAlso token.Parent.IsKind(SyntaxKind.ForEachStatement) Then
                            Return ImmutableArray.Create(DirectCast(token.Parent, ForEachStatementSyntax).Expression.GetLocation())
                        End If
                    Next
                End If
            End If

            Return ImmutableArray(Of Location).Empty
        End Function

#End Region

        ''' <summary>
        ''' Gets the top most enclosing statement as target to call MakeExplicit on.
        ''' It's either the enclosing statement, or if this statement is inside of a lambda expression, the enclosing
        ''' statement of this lambda.
        ''' </summary>
        ''' <param name="token">The token to get the complexification target for.</param>
        Public Overrides Function GetExpansionTargetForLocation(token As SyntaxToken) As SyntaxNode
            Return GetExpansionTarget(token)
        End Function

        Private Shared Function GetExpansionTarget(token As SyntaxToken) As SyntaxNode
            ' get the directly enclosing statement
            Dim enclosingStatement = token.FirstAncestorOrSelf(Function(n) TypeOf (n) Is ExecutableStatementSyntax)

            ' for nodes in a using, for or for each statement, we do not need the enclosing _executable_ statement, which is the whole block.
            ' it's enough to expand the using, for or foreach statement.
            Dim possibleSpecialStatement = token.FirstAncestorOrSelf(Function(n) n.Kind = SyntaxKind.ForStatement OrElse
                                                                                 n.Kind = SyntaxKind.ForEachStatement OrElse
                                                                                 n.Kind = SyntaxKind.UsingStatement OrElse
                                                                                 n.Kind = SyntaxKind.CatchBlock)
            If possibleSpecialStatement IsNot Nothing Then
                If enclosingStatement Is possibleSpecialStatement.Parent Then
                    enclosingStatement = If(possibleSpecialStatement.Kind = SyntaxKind.CatchBlock,
                                                DirectCast(possibleSpecialStatement, CatchBlockSyntax).CatchStatement,
                                                possibleSpecialStatement)
                End If
            End If

            ' see if there's an enclosing lambda expression
            Dim possibleLambdaExpression As SyntaxNode = Nothing
            If enclosingStatement Is Nothing Then
                possibleLambdaExpression = token.FirstAncestorOrSelf(Function(n) TypeOf (n) Is LambdaExpressionSyntax)
            End If

            Dim enclosingCref = token.FirstAncestorOrSelf(Function(n) TypeOf (n) Is CrefReferenceSyntax)
            If enclosingCref IsNot Nothing Then
                Return enclosingCref
            End If

            ' there seems to be no statement above this one. Let's see if we can at least get an SimpleNameSyntax
            Return If(enclosingStatement, If(possibleLambdaExpression, token.FirstAncestorOrSelf(Function(n) TypeOf (n) Is SimpleNameSyntax)))
        End Function

        Public Overrides Function IsRenamableTokenInComment(token As SyntaxToken) As Boolean
            Return token.IsKind(SyntaxKind.XmlTextLiteralToken, SyntaxKind.XmlNameToken)
        End Function

#Region "Helper Methods"
        Public Overrides Function IsIdentifierValid(replacementText As String, syntaxFactsService As ISyntaxFactsService) As Boolean
            replacementText = SyntaxFacts.MakeHalfWidthIdentifier(replacementText)
            Dim possibleIdentifier As String
            If syntaxFactsService.IsTypeCharacter(replacementText.Last()) Then
                ' We don't allow to use identifiers with type characters
                Return False
            Else
                If replacementText.StartsWith("[", StringComparison.Ordinal) AndAlso replacementText.EndsWith("]", StringComparison.Ordinal) Then
                    possibleIdentifier = replacementText
                Else
                    possibleIdentifier = "[" & replacementText & "]"
                End If
            End If

            ' Make sure we got an identifier. 
            If Not syntaxFactsService.IsValidIdentifier(possibleIdentifier) Then
                ' We still don't have an identifier, so let's fail
                Return False
            End If

            ' This is a valid Identifier
            Return True
        End Function

        Public Overrides Function ComputePossibleImplicitUsageConflicts(
            renamedSymbol As ISymbol,
            semanticModel As SemanticModel,
            originalDeclarationLocation As Location,
            newDeclarationLocationStartingPosition As Integer,
            cancellationToken As CancellationToken) As ImmutableArray(Of Location)

            ' TODO: support other implicitly used methods like dispose
            If CaseInsensitiveComparison.Equals(renamedSymbol.Name, "MoveNext") OrElse
                    CaseInsensitiveComparison.Equals(renamedSymbol.Name, "GetEnumerator") OrElse
                    CaseInsensitiveComparison.Equals(renamedSymbol.Name, "Current") Then

                If TypeOf renamedSymbol Is IMethodSymbol Then
                    If DirectCast(renamedSymbol, IMethodSymbol).IsOverloads AndAlso
                            (renamedSymbol.GetAllTypeArguments().Length <> 0 OrElse
                            DirectCast(renamedSymbol, IMethodSymbol).Parameters.Length <> 0) Then
                        Return ImmutableArray(Of Location).Empty
                    End If
                End If

                If TypeOf renamedSymbol Is IPropertySymbol Then
                    If DirectCast(renamedSymbol, IPropertySymbol).IsOverloads Then
                        Return ImmutableArray(Of Location).Empty
                    End If
                End If

                ' TODO: Partial methods currently only show the location where the rename happens As a conflict.
                '       Consider showing both locations as a conflict.

                Dim baseType = renamedSymbol.ContainingType?.GetBaseTypes().FirstOrDefault()
                If baseType IsNot Nothing Then
                    Dim implicitSymbols = semanticModel.LookupSymbols(
                            newDeclarationLocationStartingPosition,
                            baseType,
                            renamedSymbol.Name) _
                                .Where(Function(sym) Not sym.Equals(renamedSymbol))

                    For Each symbol In implicitSymbols
                        If symbol.GetAllTypeArguments().Length <> 0 Then
                            Continue For
                        End If

                        If symbol.Kind = SymbolKind.Method Then
                            Dim method = DirectCast(symbol, IMethodSymbol)

                            If CaseInsensitiveComparison.Equals(symbol.Name, "MoveNext") Then
                                If Not method.ReturnsVoid AndAlso Not method.Parameters.Any() AndAlso method.ReturnType.SpecialType = SpecialType.System_Boolean Then
                                    Return ImmutableArray.Create(originalDeclarationLocation)
                                End If
                            ElseIf CaseInsensitiveComparison.Equals(symbol.Name, "GetEnumerator") Then
                                ' we are a bit pessimistic here. 
                                ' To be sure we would need to check if the returned type Is having a MoveNext And Current as required by foreach
                                If Not method.ReturnsVoid AndAlso
                                        Not method.Parameters.Any() Then
                                    Return ImmutableArray.Create(originalDeclarationLocation)
                                End If
                            End If

                        ElseIf CaseInsensitiveComparison.Equals(symbol.Name, "Current") Then
                            Dim [property] = DirectCast(symbol, IPropertySymbol)

                            If Not [property].Parameters.Any() AndAlso Not [property].IsWriteOnly Then
                                Return ImmutableArray.Create(originalDeclarationLocation)
                            End If
                        End If
                    Next
                End If
            End If

            Return ImmutableArray(Of Location).Empty
        End Function

        Public Overrides Sub TryAddPossibleNameConflicts(symbol As ISymbol, replacementText As String, possibleNameConflicts As ICollection(Of String))
            Dim halfWidthReplacementText = SyntaxFacts.MakeHalfWidthIdentifier(replacementText)

            Const AttributeSuffix As String = "Attribute"
            Const AttributeSuffixLength As Integer = 9
            Debug.Assert(AttributeSuffixLength = AttributeSuffix.Length, "Assert (AttributeSuffixLength = AttributeSuffix.Length) failed.")

            If replacementText.Length > AttributeSuffixLength AndAlso CaseInsensitiveComparison.Equals(halfWidthReplacementText.Substring(halfWidthReplacementText.Length - AttributeSuffixLength), AttributeSuffix) Then
                Dim conflict = replacementText.Substring(0, replacementText.Length - AttributeSuffixLength)
                If Not possibleNameConflicts.Contains(conflict) Then
                    possibleNameConflicts.Add(conflict)
                End If
            End If

            If symbol.Kind = SymbolKind.Property Then
                For Each conflict In {"_" + replacementText, "get_" + replacementText, "set_" + replacementText}
                    If Not possibleNameConflicts.Contains(conflict) Then
                        possibleNameConflicts.Add(conflict)
                    End If
                Next
            End If

            ' consider both versions of the identifier (escaped and unescaped)
            Dim valueText = replacementText
            Dim kind = SyntaxFacts.GetKeywordKind(replacementText)
            If kind <> SyntaxKind.None Then
                valueText = SyntaxFacts.GetText(kind)
            Else
                Dim name = SyntaxFactory.ParseName(replacementText)
                If name.Kind = SyntaxKind.IdentifierName Then
                    valueText = DirectCast(name, IdentifierNameSyntax).Identifier.ValueText
                End If
            End If

            If Not CaseInsensitiveComparison.Equals(valueText, replacementText) Then
                possibleNameConflicts.Add(valueText)
            End If
        End Sub

        ''' <summary>
        ''' Gets the semantic model for the given node. 
        ''' If the node belongs to the syntax tree of the original semantic model, then returns originalSemanticModel.
        ''' Otherwise, returns a speculative model.
        ''' The assumption for the later case is that span start position of the given node in it's syntax tree is same as
        ''' the span start of the original node in the original syntax tree.
        ''' </summary>
        ''' <param name="node"></param>
        ''' <param name="originalSemanticModel"></param>
        Public Shared Function GetSemanticModelForNode(node As SyntaxNode, originalSemanticModel As SemanticModel) As SemanticModel
            If node.SyntaxTree Is originalSemanticModel.SyntaxTree Then
                ' This is possible if the previous rename phase didn't rewrite any nodes in this tree.
                Return originalSemanticModel
            End If

            Dim syntax = node
            Dim nodeToSpeculate = syntax.GetAncestorsOrThis(Of SyntaxNode).Where(Function(n) SpeculationAnalyzer.CanSpeculateOnNode(n)).LastOrDefault
            If nodeToSpeculate Is Nothing Then
                If syntax.IsKind(SyntaxKind.CrefReference) Then
                    nodeToSpeculate = DirectCast(syntax, CrefReferenceSyntax).Name
                ElseIf syntax.IsKind(SyntaxKind.TypeConstraint) Then
                    nodeToSpeculate = DirectCast(syntax, TypeConstraintSyntax).Type
                Else
                    Return Nothing
                End If
            End If

            Dim isInNamespaceOrTypeContext = SyntaxFacts.IsInNamespaceOrTypeContext(TryCast(syntax, ExpressionSyntax))
            Dim position = nodeToSpeculate.SpanStart
            Return SpeculationAnalyzer.CreateSpeculativeSemanticModelForNode(nodeToSpeculate, DirectCast(originalSemanticModel, SemanticModel), position, isInNamespaceOrTypeContext)
        End Function
#End Region

    End Class

End Namespace
