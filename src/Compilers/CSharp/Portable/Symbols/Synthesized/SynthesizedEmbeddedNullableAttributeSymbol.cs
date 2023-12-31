﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal sealed class SynthesizedEmbeddedNullableAttributeSymbol : SynthesizedEmbeddedAttributeSymbolBase
    {
        private readonly ImmutableArray<FieldSymbol> _fields;
        private readonly ImmutableArray<MethodSymbol> _constructors;
        private readonly TypeSymbol _byteTypeSymbol;

        private const string NullableFlagsFieldName = "NullableFlags";

        public SynthesizedEmbeddedNullableAttributeSymbol(
            string name,
            NamespaceSymbol containingNamespace,
            ModuleSymbol containingModule,
            NamedTypeSymbol systemAttributeType,
            TypeSymbol systemByteType)
            : base(name, containingNamespace, containingModule, baseType: systemAttributeType)
        {
            _byteTypeSymbol = systemByteType;

            var annotatedByteType = TypeWithAnnotations.Create(systemByteType);

            var byteArrayType = TypeWithAnnotations.Create(
                ArrayTypeSymbol.CreateSZArray(
                    systemByteType.ContainingAssembly,
                    annotatedByteType));

            _fields = ImmutableArray.Create<FieldSymbol>(
                new SynthesizedFieldSymbol(
                    this,
                    byteArrayType.Type,
                    NullableFlagsFieldName,
                    isPublic: true,
                    isReadOnly: true,
                    isStatic: false));

            _constructors = ImmutableArray.Create<MethodSymbol>(
                new SynthesizedEmbeddedAttributeConstructorWithBodySymbol(
                    this,
                    m => ImmutableArray.Create(SynthesizedParameterSymbol.Create(m, annotatedByteType, 0, RefKind.None)),
                    GenerateSingleByteConstructorBody),
                new SynthesizedEmbeddedAttributeConstructorWithBodySymbol(
                    this,
                    m => ImmutableArray.Create(SynthesizedParameterSymbol.Create(m, byteArrayType, 0, RefKind.None)),
                    GenerateByteArrayConstructorBody));

            // Ensure we never get out of sync with the description
            Debug.Assert(_constructors.Length == AttributeDescription.NullableAttribute.Signatures.Length);
        }

        internal override IEnumerable<FieldSymbol> GetFieldsToEmit() => _fields;

        public override ImmutableArray<MethodSymbol> Constructors => _constructors;

        internal override AttributeUsageInfo GetAttributeUsageInfo()
        {
            return new AttributeUsageInfo(
                AttributeTargets.Class | AttributeTargets.Event | AttributeTargets.Field | AttributeTargets.GenericParameter | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue,
                allowMultiple: false,
                inherited: false);
        }

        private void GenerateByteArrayConstructorBody(SyntheticBoundNodeFactory factory, ArrayBuilder<BoundStatement> statements, ImmutableArray<ParameterSymbol> parameters)
        {
            statements.Add(
                factory.ExpressionStatement(
                    factory.AssignmentExpression(
                        factory.Field(
                            factory.This(),
                            _fields.Single()),
                        factory.Parameter(parameters.Single())
                    )
                )
            );
        }

        private void GenerateSingleByteConstructorBody(SyntheticBoundNodeFactory factory, ArrayBuilder<BoundStatement> statements, ImmutableArray<ParameterSymbol> parameters)
        {
            statements.Add(
                factory.ExpressionStatement(
                    factory.AssignmentExpression(
                        factory.Field(
                            factory.This(),
                            _fields.Single()),
                        factory.Array(
                            _byteTypeSymbol,
                            ImmutableArray.Create<BoundExpression>(
                                factory.Parameter(parameters.Single())
                            )
                        )
                    )
                )
            );
        }
    }

    internal sealed class SynthesizedEmbeddedAttributeConstructorWithBodySymbol : SynthesizedInstanceConstructor
    {
        private readonly ImmutableArray<ParameterSymbol> _parameters;

        private readonly Action<SyntheticBoundNodeFactory, ArrayBuilder<BoundStatement>, ImmutableArray<ParameterSymbol>> _getConstructorBody;

        internal SynthesizedEmbeddedAttributeConstructorWithBodySymbol(
            NamedTypeSymbol containingType,
            Func<MethodSymbol, ImmutableArray<ParameterSymbol>> getParameters,
            Action<SyntheticBoundNodeFactory, ArrayBuilder<BoundStatement>, ImmutableArray<ParameterSymbol>> getConstructorBody) :
            base(containingType)
        {
            _parameters = getParameters(this);
            _getConstructorBody = getConstructorBody;
        }

        public override ImmutableArray<ParameterSymbol> Parameters => _parameters;

        internal override void GenerateMethodBody(TypeCompilationState compilationState, BindingDiagnosticBag diagnostics)
        {
            GenerateMethodBodyCore(compilationState, diagnostics);
        }

        internal override void GenerateMethodBodyStatements(SyntheticBoundNodeFactory factory, ArrayBuilder<BoundStatement> statements, BindingDiagnosticBag diagnostics) => _getConstructorBody(factory, statements, _parameters);
    }
}

