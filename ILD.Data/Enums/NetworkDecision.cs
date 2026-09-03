namespace ILD.Data.Enums;

/// <summary>
/// What the egress proxy did with one connection. <see cref="Advisory"/> is the
/// outcome while the filter mode is <c>off</c>: the destination was recorded but
/// no list was consulted.
/// </summary>
public enum NetworkDecision
{
    Allowed,
    Blocked,
    Advisory,
}
