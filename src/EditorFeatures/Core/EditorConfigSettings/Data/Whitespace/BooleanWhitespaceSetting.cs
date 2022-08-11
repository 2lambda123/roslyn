﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.EditorConfigSettings;
using Microsoft.CodeAnalysis.Editor.EditorConfigSettings.Data;
using Microsoft.CodeAnalysis.Editor.EditorConfigSettings.Updater;
using Microsoft.CodeAnalysis.Options;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.EditorConfigSettings.Data.Whitespace
{
    internal class BooleanWhitespaceSetting : WhitespaceSetting<bool>
    {
        public BooleanWhitespaceSetting(Option2<bool> option,
                                    string description,
                                    AnalyzerConfigOptions editorConfigOptions,
                                    OptionSet visualStudioOptions,
                                    OptionUpdater updater,
                                    SettingLocation fileName)
            : base(option, description, editorConfigOptions, visualStudioOptions, updater, fileName)
        {
        }

        public override ImmutableArray<string>? GetSettingValues(OptionSet _)
        {
            var storageLocation = GetEditorConfigStorageLocation();
            return GetBooleanSettingValuesHelper(storageLocation);
        }
    }
}
