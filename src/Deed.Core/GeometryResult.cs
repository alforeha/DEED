namespace Deed.Core;

public enum GeometryError
{
    EmptyInput,
    InvalidBearingFormat,
    InvalidPrefix,
    InvalidSuffix,
    InvalidDegrees,
    InvalidMinutes,
    InvalidSeconds,
    NegativeDistance,
    NonFiniteValue
}

public readonly record struct GeometryDiagnostic(GeometryError Code, string Message);

public readonly struct GeometryResult<T>
{
    private readonly T? _value;

    private GeometryResult(T value)
    {
        _value = value;
        IsSuccess = true;
        Diagnostic = null;
    }

    private GeometryResult(GeometryDiagnostic diagnostic)
    {
        _value = default;
        IsSuccess = false;
        Diagnostic = diagnostic;
    }

    public bool IsSuccess { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    public GeometryDiagnostic? Diagnostic { get; }

    public static GeometryResult<T> Success(T value) => new(value);

    public static GeometryResult<T> Failure(GeometryError code, string message) =>
        new(new GeometryDiagnostic(code, message));
}
