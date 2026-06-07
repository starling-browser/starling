// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// How a layer or fill composites over what is below it. SrcOver is the default
/// source-over blend. The separable blend modes match the cascading style sheet
/// (CSS) mix-blend-mode set the document surface needs.
/// </summary>
public enum BlendMode
{
    SrcOver,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
}
