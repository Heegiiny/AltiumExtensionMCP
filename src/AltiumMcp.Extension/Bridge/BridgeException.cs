using System;
using System.Collections.Generic;
using AltiumMcp.Contracts.Bridge;

namespace AltiumMcp.Extension.Bridge;

/// <summary>Domain error with a stable code; converted to <see cref="BridgeError"/> on the wire.</summary>
public sealed class BridgeException : Exception
{
    public string Code { get; }

    public Dictionary<string, string>? Details { get; }

    public BridgeException(string code, string message, Dictionary<string, string>? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public BridgeError ToError() => new(Code, Message, Details);
}
