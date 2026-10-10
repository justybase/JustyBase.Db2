namespace JustyBase.Db2.AdoNet;

/// <summary>Provider-specific information notification for successful Db2 warnings.</summary>
public sealed class Db2AdoInfoMessageEventArgs : EventArgs
{
    internal Db2AdoInfoMessageEventArgs(IEnumerable<Db2Warning> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    /// <summary>Warnings returned with the successful operation, in server order.</summary>
    public IReadOnlyList<Db2Warning> Errors { get; }
}
