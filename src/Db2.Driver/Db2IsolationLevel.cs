namespace JustyBase.Db2;

/// <summary>Db2 LUW isolation for dynamically prepared SQL; Unspecified retains the existing setting.</summary>
public enum Db2IsolationLevel { Unspecified, UncommittedRead, CursorStability, ReadStability, RepeatableRead }
