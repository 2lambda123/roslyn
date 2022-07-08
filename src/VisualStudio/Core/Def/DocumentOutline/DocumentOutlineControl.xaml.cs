﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices.Implementation;
using Microsoft.VisualStudio.LanguageServices.Implementation.LanguageServiceBrokerShim;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.DocumentOutline
{
    /// <summary>
    /// Interaction logic for DocumentOutlineControl.xaml
    /// </summary>
    internal partial class DocumentOutlineControl : UserControl, IVsCodeWindowEvents
    {
        private ILanguageServiceBrokerShim LanguageServiceBroker { get; }

        private IThreadingContext ThreadingContext { get; }

        private IVsEditorAdaptersFactoryService EditorAdaptersFactoryService { get; }

        private IVsCodeWindow CodeWindow { get; }

        /// <summary>
        /// The type of sorting to be applied to the UI model in <see cref="UpdateUIAsync"/>.
        /// </summary>
        private SortOption SortOption { get; set; }

        /// <summary>
        /// Queue to batch up work to do to compute the UI model. Used so we can batch up a lot of events 
        /// and only fetch the model once for every batch.
        /// </summary>
        private readonly AsyncBatchingWorkQueue<bool, DocumentSymbolModel?> _computeUIModelQueue;

        /// <summary>
        /// Queue to batch up work to do to update the UI model.
        /// </summary>
        private readonly AsyncBatchingWorkQueue<bool, DocumentSymbolModel?> _updateUIModelQueue;

        /// <summary>
        /// Queue to batch up work to do to highlight the currently selected symbol node and update the UI.
        /// </summary>
        private readonly AsyncBatchingWorkQueue _highlightNodeAndUpdateUIQueue;

        /// <summary>
        /// Queue to batch up work to do to select code in the editor based on the current caret position.
        /// </summary>
        private readonly AsyncBatchingWorkQueue<DocumentSymbolItem> _jumpToContentQueue;

        /// <summary>
        /// Keeps track of the current primary and secondary text views. Should only be accessed by the UI thread.
        /// </summary>
        private readonly Dictionary<IVsTextView, ITextView> _trackedTextViews = new();

        public DocumentOutlineControl(
            ILanguageServiceBrokerShim languageServiceBroker,
            IThreadingContext threadingContext,
            IAsynchronousOperationListener asyncListener,
            IVsEditorAdaptersFactoryService editorAdaptersFactoryService,
            IVsCodeWindow codeWindow,
            CancellationToken cancellationToken)
        {
            InitializeComponent();

            LanguageServiceBroker = languageServiceBroker;
            ThreadingContext = threadingContext;
            EditorAdaptersFactoryService = editorAdaptersFactoryService;
            CodeWindow = codeWindow;
            ComEventSink.Advise<IVsCodeWindowEvents>(codeWindow, this);
            SortOption = SortOption.Order;

            _computeUIModelQueue = new AsyncBatchingWorkQueue<bool, DocumentSymbolModel?>(
                DelayTimeSpan.Short,
                ComputeUIModelAsync,
                EqualityComparer<bool>.Default,
                asyncListener,
                cancellationToken);

            _updateUIModelQueue = new AsyncBatchingWorkQueue<bool, DocumentSymbolModel?>(
                DelayTimeSpan.NearImmediate,
                UpdateUIAsync,
                EqualityComparer<bool>.Default,
                asyncListener,
                cancellationToken);

            _highlightNodeAndUpdateUIQueue = new AsyncBatchingWorkQueue(
                DelayTimeSpan.NearImmediate,
                HightlightNodeAsync,
                asyncListener,
                cancellationToken);

            _jumpToContentQueue = new AsyncBatchingWorkQueue<DocumentSymbolItem>(
                DelayTimeSpan.NearImmediate,
                JumpToContentAsync,
                asyncListener,
                cancellationToken);

            // Primary text view is expected to exist on window initialization.
            if (ErrorHandler.Failed(codeWindow.GetPrimaryView(out var primaryTextView)))
                Debug.Fail("GetPrimaryView failed during DocumentOutlineControl initialization.");

            if (ErrorHandler.Failed(StartTrackingView(primaryTextView)))
                Debug.Fail("StartTrackingView failed during DocumentOutlineControl initialization.");

            if (ErrorHandler.Succeeded(codeWindow.GetSecondaryView(out var secondaryTextView)))
            {
                if (ErrorHandler.Failed(StartTrackingView(secondaryTextView)))
                    Debug.Fail("StartTrackingView failed during DocumentOutlineControl initialization.");
            }

            StartComputeUIModelTask();
        }

        int IVsCodeWindowEvents.OnNewView(IVsTextView pView)
        {
            ThreadingContext.ThrowIfNotOnUIThread();
            StartTrackingView(pView);
            return VSConstants.S_OK;
        }

        private int StartTrackingView(IVsTextView textView)
        {
            ThreadingContext.ThrowIfNotOnUIThread();

            var wpfTextView = EditorAdaptersFactoryService.GetWpfTextView(textView);
            if (wpfTextView is null)
                return VSConstants.E_FAIL;

            _trackedTextViews.Add(textView, wpfTextView);

            wpfTextView.Caret.PositionChanged += Caret_PositionChanged;

            // Subscribe only once since text buffer is the same for the primary and secondary text views.
            if (_trackedTextViews.Count == 1)
                wpfTextView.TextBuffer.Changed += TextBuffer_Changed;

            return VSConstants.S_OK;
        }

        int IVsCodeWindowEvents.OnCloseView(IVsTextView pView)
        {
            ThreadingContext.ThrowIfNotOnUIThread();

            if (_trackedTextViews.TryGetValue(pView, out var view))
            {
                view.Caret.PositionChanged -= Caret_PositionChanged;

                // Unsubscribe only once since text buffer is the same for the primary and secondary text views.
                if (_trackedTextViews.Count == 1)
                    view.TextBuffer.Changed -= TextBuffer_Changed;

                _trackedTextViews.Remove(pView);
            }

            return VSConstants.S_OK;
        }

        /// <summary>
        /// On text buffer change, obtain an updated UI model and update the view.
        /// </summary>
        private void TextBuffer_Changed(object sender, TextContentChangedEventArgs e)
            => StartComputeUIModelTask();

        /// <summary>
        /// On caret position change in a text view, highlight the corresponding symbol node in the window.
        /// </summary>
        private void Caret_PositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            if (!e.NewPosition.Equals(e.OldPosition))
                StartHightlightNodeTask();
        }

        private void ExpandAll(object sender, RoutedEventArgs e)
        {
            DocumentOutlineHelper.SetIsExpanded((IEnumerable<DocumentSymbolItem>)symbolTree.ItemsSource, true);
        }

        private void CollapseAll(object sender, RoutedEventArgs e)
        {
            DocumentOutlineHelper.SetIsExpanded((IEnumerable<DocumentSymbolItem>)symbolTree.ItemsSource, false);
        }

        private void Search(object sender, EventArgs e)
        {
            StartUpdateUIModelTask();
        }

        private void SortByName(object sender, EventArgs e)
        {
            SortOption = SortOption.Name;
            StartUpdateUIModelTask();
        }

        private void SortByOrder(object sender, EventArgs e)
        {
            SortOption = SortOption.Order;
            StartUpdateUIModelTask();
        }

        private void SortByType(object sender, EventArgs e)
        {
            SortOption = SortOption.Type;
            StartUpdateUIModelTask();
        }

        /// <summary>
        /// When a symbol node in the window is clicked, move the caret to its position in the latest active text view.
        /// </summary>
        private void JumpToContent(object sender, EventArgs e)
        {
            if (sender is StackPanel panel && panel.DataContext is DocumentSymbolItem symbol)
                StartJumpToContent(symbol);
        }
    }
}
