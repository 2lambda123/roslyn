﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.EditorConfigSettings.Updater;
using Microsoft.CodeAnalysis.EditorConfig;
using Microsoft.CodeAnalysis.EditorConfigSettings.Data;
using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.Editor.EditorConfigSettings.Data
{
    internal class AnalyzerSetting : IEditorConfigSettingInfo
    {
        private readonly DiagnosticDescriptor _descriptor;
        private readonly AnalyzerSettingsUpdater _settingsUpdater;

        public AnalyzerSetting(DiagnosticDescriptor descriptor,
                               ReportDiagnostic effectiveSeverity,
                               AnalyzerSettingsUpdater settingsUpdater,
                               Language language,
                               SettingLocation location)
        {
            _descriptor = descriptor;
            _settingsUpdater = settingsUpdater;
            DiagnosticSeverity severity = default;
            if (effectiveSeverity == ReportDiagnostic.Default)
            {
                severity = descriptor.DefaultSeverity;
            }
            else if (effectiveSeverity.ToDiagnosticSeverity() is DiagnosticSeverity severity1)
            {
                severity = severity1;
            }

            var enabled = effectiveSeverity != ReportDiagnostic.Suppress;
            IsEnabled = enabled;
            Severity = severity;
            Language = language;
            IsNotConfigurable = descriptor.CustomTags.Any(t => t == WellKnownDiagnosticTags.NotConfigurable);
            Location = location;
        }

        public string Id => _descriptor.Id;
        public string Title => _descriptor.Title.ToString(CultureInfo.CurrentUICulture);
        public string Description => _descriptor.Description.ToString(CultureInfo.CurrentUICulture);
        public string Category => _descriptor.Category;
        public DiagnosticSeverity Severity { get; private set; }
        public bool IsEnabled { get; private set; }
        public Language Language { get; }
        public bool IsNotConfigurable { get; set; }
        public SettingLocation Location { get; }

        internal void ChangeSeverity(DiagnosticSeverity severity)
        {
            if (severity == Severity)
                return;

            Severity = severity;
            _settingsUpdater.QueueUpdate(this, severity);
        }

        public string GetSettingName()
        {
            return $"dotnet_diagnostic.{Id}.severity";
        }

        public string GetDocumentation()
        {
            return Description;
        }

        public ImmutableArray<string>? GetSettingValues(OptionSet _)
        {
            return ImmutableArray.Create(new string[] { "none", "silent", "suggestion", "warning", "error" });
        }
    }
}
