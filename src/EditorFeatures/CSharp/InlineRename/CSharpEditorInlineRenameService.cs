﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Composition;
using Microsoft.CodeAnalysis.Editor.Implementation.InlineRename;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.Editor.CSharp.InlineRename
{
    [ExportLanguageService(typeof(IEditorInlineRenameService), LanguageNames.CSharp), Shared]
    internal sealed class CSharpEditorInlineRenameService : AbstractEditorInlineRenameService
    {
        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public CSharpEditorInlineRenameService(
            [ImportMany] IEnumerable<IRefactorNotifyService> refactorNotifyServices,
            IGlobalOptionService globalOptions)
            : base(refactorNotifyServices, globalOptions)
        {
        }
    }
}
