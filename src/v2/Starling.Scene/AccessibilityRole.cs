// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>The role of an accessibility node. A small subset to start; it grows with the generated-UI runtime in Phase 4.</summary>
public enum AccessibilityRole
{
    None,
    Document,
    Group,
    Heading,
    Button,
    Link,
    StaticText,
    Image,
    List,
    ListItem,
    TextField,
}
