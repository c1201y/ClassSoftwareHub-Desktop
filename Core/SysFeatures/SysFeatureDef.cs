using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Core;


public enum SysFeatureKind
{
    
    Toggle,

    
    Choice,
}


public sealed record SysChoice(string Label, int Value);






















public sealed class SysFeatureDef
{
    
    public required string Id { get; init; }

    
    public required string Group { get; init; }

    
    public required string Header { get; init; }

    
    public string? Description { get; init; }

    
    public string Glyph { get; init; } = "\uE7E8";

    public SysFeatureKind Kind { get; init; } = SysFeatureKind.Toggle;

    
    public IReadOnlyList<SysChoice>? Choices { get; init; }

    
    
    
    
    public required Func<int?> Read { get; init; }

    
    
    
    
    public required Func<int, IReadOnlyList<string>> Command { get; init; }

    
    public required Func<int, string> Label { get; init; }

    
    public bool NeedsRestart { get; init; }

    
    public bool LaptopOnly { get; init; }

    
    public string? Confirm { get; init; }
}
