// SPDX-License-Identifier: Apache-2.0
namespace System.Runtime.CompilerServices;

// C# 15 union types need these two support types. Early .NET 11 previews do not ship
// them in the runtime, so they are declared here (the language reference shows exactly
// this declaration). CS0436 is suppressed for this project, so this still compiles once
// a later .NET 11 preview adds them to the runtime — the source copy simply wins.

/// <summary>Marks a struct or class as a C# union type. Polyfill for early .NET 11 previews.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class UnionAttribute : Attribute;

/// <summary>The interface every union type implements. Polyfill for early .NET 11 previews.</summary>
public interface IUnion
{
    object? Value { get; }
}
