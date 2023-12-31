﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Generic;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.UnitTests.UtilityTest
{
    public class BKTreeTests
    {
        [Fact]
        public void SimpleTests()
        {
            string[] testValues = { "cook", "book", "books", "cake", "what", "water", "Cape", "Boon", "Cook", "Cart" };
            var tree = BKTree.Create(testValues);

            var results1 = tree.Find("wat", threshold: 1);
            Assert.Single(results1, "what");

            var results2 = tree.Find("wat", threshold: 2);
            Assert.True(results2.SetEquals(Expected("cart", "what", "water")));

            var results3 = tree.Find("caqe", threshold: 1);
            Assert.True(results3.SetEquals(Expected("cake", "cape")));
        }

        [Fact]
        public void PermutationTests()
        {
            string[] testValues = { "cook", "book", "books", "cake", "what", "water", "Cape", "Boon", "Cook", "Cart" };
            TestTreeInvariants(testValues);
        }

        private static void TestTreeInvariants(string[] testValues)
        {
            var tree = BKTree.Create(testValues);

            foreach (var value in testValues)
            {
                // With a threshold of 0, we should only find exactly the item we're searching for.
                Assert.Single(tree.Find(value, threshold: 0), value.ToLower());
            }

            foreach (var value in testValues)
            {
                // With a threshold of 1, we should always at least find the item we're looking for.
                // But we may also find additional items along with it.
                var items = tree.Find(value, threshold: 1);
                Assert.Contains(value.ToLower(), items);

                // We better not be finding all items.
                Assert.NotEqual(testValues.Length, items.Count);
            }

            foreach (var value in testValues)
            {
                // If we delete each individual character in each search string, we should still
                // find the value in the tree.
                for (var i = 0; i < value.Length; i++)
                {
                    var items = tree.Find(Delete(value, i), threshold: null);
                    Assert.Contains(value.ToLower(), items);

                    // We better not be finding all items.
                    Assert.NotEqual(testValues.Length, items.Count);
                }
            }

            foreach (var value in testValues)
            {
                // If we add a random character at any location in a string, we should still 
                // be able to find it.
                for (var i = 0; i <= value.Length; i++)
                {
                    var items = tree.Find(Insert(value, i, 'Z'), threshold: null);
                    Assert.Contains(value.ToLower(), items);

                    // We better not be finding all items.
                    Assert.NotEqual(testValues.Length, items.Count);
                }
            }

            foreach (var value in testValues)
            {
                // If we transpose any characters in a string, we should still 
                // be able to find it.
                for (var i = 0; i < value.Length - 1; i++)
                {
                    var items = tree.Find(Transpose(value, i), threshold: null);
                    Assert.Contains(value.ToLower(), items);
                }
            }
        }

        private static string Transpose(string value, int i)
            => value[..i] + value[i + 1] + value[i] + value[(i + 2)..];

        private static string Insert(string value, int i, char v)
            => value[..i] + v + value[i..];

        private static string Delete(string value, int i)
            => value[..i] + value[(i + 1)..];

        [Fact]
        public void Test2()
        {
            string[] testValues = { "Leeds", "York", "Bristol", "Leicester", "Hull", "Durham" };
            var tree = BKTree.Create(testValues);

            var results = tree.Find("hill", threshold: null);
            Assert.True(results.SetEquals(Expected("hull")));

            results = tree.Find("liecester", threshold: null);
            Assert.True(results.SetEquals(Expected("leicester")));

            results = tree.Find("leicestre", threshold: null);
            Assert.True(results.SetEquals(Expected("leicester")));

            results = tree.Find("lecester", threshold: null);
            Assert.True(results.SetEquals(Expected("leicester")));
        }

        [Fact]
        public void TestSpillover()
        {
            string[] testValues = {
                /*root:*/ "Four",
                /*d=1*/ "Fou", "For", "Fur", "Our", "FourA", "FouAr", "FoAur", "FAour", "AFour", "Tour",
                /*d=2*/ "Fo", "Fu", "Fr", "or", "ur", "ou", "FourAb", "FouAbr", "FoAbur", "FAbour", "AbFour", "oFour", "Fuor", "Foru", "ours",
                /*d=3*/ "F", "o", "u", "r", "Fob", "Fox", "bur", "urn", "hur", "foraa", "found"
            };
            TestTreeInvariants(testValues);
        }

        [Fact]
        public void Top1000()
            => TestTreeInvariants(EditDistanceTests.Top1000);

        private static IEnumerable<string> Expected(params string[] values)
            => values;
    }
}
