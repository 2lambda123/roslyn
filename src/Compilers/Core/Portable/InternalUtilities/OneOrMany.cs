﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Roslyn.Utilities
{
    /// <summary>
    /// Represents a single item or many items (including none).
    /// </summary>
    /// <remarks>
    /// Used when a collection usually contains a single item but sometimes might contain multiple.
    /// </remarks>
    internal readonly struct OneOrMany<T>
    {
        public static readonly OneOrMany<T> Empty = new OneOrMany<T>(ImmutableArray<T>.Empty);

        private readonly T? _one;
        private readonly ImmutableArray<T> _many;

        public OneOrMany(T one)
        {
            _one = one;
            _many = default;
        }

        public OneOrMany(ImmutableArray<T> many)
        {
            if (many.IsDefault)
            {
                throw new ArgumentNullException(nameof(many));
            }

            _one = default;
            _many = many;
        }

        [MemberNotNullWhen(true, nameof(_one))]
        private bool HasOne
            => _many.IsDefault;

        public T this[int index]
        {
            get
            {
                if (HasOne)
                {
                    if (index != 0)
                    {
                        throw new IndexOutOfRangeException();
                    }

                    return _one;
                }
                else
                {
                    return _many[index];
                }
            }
        }

        public int Count
            => HasOne ? 1 : _many.Length;

        public bool IsEmpty
            => Count == 0;

        public OneOrMany<T> Add(T one)
        {
            var builder = ArrayBuilder<T>.GetInstance(this.Count + 1);
            if (HasOne)
            {
                builder.Add(_one);
            }
            else
            {
                builder.AddRange(_many);
            }

            builder.Add(one);
            return new OneOrMany<T>(builder.ToImmutableAndFree());
        }

        public bool Contains(T item)
        {
            if (HasOne)
                return EqualityComparer<T>.Default.Equals(item, _one);

            foreach (var value in _many)
            {
                if (EqualityComparer<T>.Default.Equals(item, value))
                    return true;
            }

            return false;
        }

        public OneOrMany<T> RemoveAll(T item)
        {
            if (HasOne)
            {
                return EqualityComparer<T>.Default.Equals(item, _one) ? default : this;
            }

            var builder = ArrayBuilder<T>.GetInstance();

            foreach (var value in _many)
            {
                if (!EqualityComparer<T>.Default.Equals(item, value))
                    builder.Add(value);
            }

            if (builder.Count == 0)
            {
                builder.Free();
                return default;
            }

            return builder.Count == Count ? this : new OneOrMany<T>(builder.ToImmutableAndFree());
        }

        public OneOrMany<TResult> Select<TResult>(Func<T, TResult> selector)
        {
            return HasOne ?
                OneOrMany.Create(selector(_one)) :
                OneOrMany.Create(_many.SelectAsArray(selector));
        }

        public OneOrMany<TResult> Select<TResult, TArg>(Func<T, TArg, TResult> selector, TArg arg)
        {
            return HasOne ?
                OneOrMany.Create(selector(_one, arg)) :
                OneOrMany.Create(_many.SelectAsArray(selector, arg));
        }

        public T? FirstOrDefault(Func<T, bool> predicate)
        {
            if (HasOne)
            {
                return predicate(_one) ? _one : default;
            }

            foreach (var item in _many)
            {
                if (predicate(item))
                {
                    return item;
                }
            }

            return default;
        }

        public T? FirstOrDefault<TArg>(Func<T, TArg, bool> predicate, TArg arg)
        {
            if (HasOne)
            {
                return predicate(_one, arg) ? _one : default;
            }

            foreach (var item in _many)
            {
                if (predicate(item, arg))
                {
                    return item;
                }
            }

            return default;
        }

        public Enumerator GetEnumerator()
            => new(this);

        internal struct Enumerator
        {
            private readonly OneOrMany<T> _collection;
            private int _index;

            internal Enumerator(OneOrMany<T> collection)
            {
                _collection = collection;
                _index = -1;
            }

            public bool MoveNext()
            {
                _index++;
                return _index < _collection.Count;
            }

            public T Current => _collection[_index];
        }
    }

    internal static class OneOrMany
    {
        public static OneOrMany<T> Create<T>(T one)
            => new OneOrMany<T>(one);

        public static OneOrMany<T> Create<T>(ImmutableArray<T> many)
            => new OneOrMany<T>(many);
    }
}
