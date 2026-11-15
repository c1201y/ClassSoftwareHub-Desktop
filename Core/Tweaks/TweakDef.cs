using System;

namespace ClassSoftwareHub.Desktop.Core;








public enum TweakRefresh
{
    
    None,

    
    Tray,

    
    Desktop,
}


public enum TweakScope
{
    
    
    
    
    
    
    
    
    
    User,

    
    
    
    System,
}


public sealed record TweakChoice(string Label, int Value);


public enum TweakRisk
{
    Normal,
    Caution,
}



















public sealed record TweakDef
{
    public required string Id { get; init; }

    
    public required string Group { get; init; }

    
    public required string Header { get; init; }

    public string Description { get; init; } = "";

    
    public TweakScope Scope { get; init; } = TweakScope.User;

    
    public required string KeyPath { get; init; }

    public required string ValueName { get; init; }

    
    public required int OnValue { get; init; }

    
    public required int OffValue { get; init; }

    public TweakRefresh Refresh { get; init; } = TweakRefresh.None;

    
    
    
    
    
    
    
    
    
    
    
    
    public IReadOnlyList<TweakChoice> Choices { get; init; } = Array.Empty<TweakChoice>();

    
    public bool IsChoice => Choices.Count > 0;

    
    
    
    
    
    
    
    
    public int MinBuild { get; init; }

    
    public int MaxBuild { get; init; }

    public TweakRisk Risk { get; init; } = TweakRisk.Normal;

    
    
    
    
    
    
    
    
    
    
    
    
    public bool DeleteWhenOff { get; init; }

    
    public string FullPath => $@"{RootName}\{KeyPath}\{ValueName}";

    
    public string RootName => Scope == TweakScope.System ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
}
