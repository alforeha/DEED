namespace Deed.Core;

public enum DiagnosticSeverity { Error, Warning }

public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message,
    string? EntityType = null, string? EntityId = null);

public readonly struct DomainResult<T>
{
    private readonly T? _value;
    private DomainResult(T value) { _value = value; Diagnostic = null; }
    private DomainResult(Diagnostic diagnostic) { _value = default; Diagnostic = diagnostic; }
    public bool IsSuccess => Diagnostic is null;
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result has no value.");
    public Diagnostic? Diagnostic { get; }
    public static DomainResult<T> Success(T value) => new(value);
    public static DomainResult<T> Failure(string code, string message) =>
        new(new Diagnostic(code, DiagnosticSeverity.Error, message));
}

public static class DiagnosticCodes
{
    public const string InvalidId = "structure.invalid_id";
    public const string IdMismatch = "structure.id_mismatch";
    public const string InvalidSettings = "structure.invalid_settings";
    public const string DanglingReference = "structure.dangling_reference";
    public const string CourseMembership = "structure.course_membership";
    public const string DuplicateCourseMembership = "structure.duplicate_course_membership";
    public const string OrphanNode = "structure.orphan_node";
    public const string CourseEndMismatch = "structure.course_end_mismatch";
    public const string InvalidBlockOrigin = "structure.invalid_block_origin";
    public const string DisconnectedBlock = "structure.disconnected_block";
    public const string NonFiniteCoordinate = "geometry.non_finite_coordinate";
    public const string IncompleteCourse = "geometry.incomplete_course";
    public const string CircularDependency = "geometry.circular_dependency";
    public const string UnresolvedDependency = "geometry.unresolved_dependency";
    public const string InvalidDraftedCompletion = "geometry.invalid_drafted_completion";
    public const string FixedEndMismatch = "geometry.fixed_end_mismatch";
    public const string IdCounterBehind = "structure.id_counter_behind";
    public const string IdSpaceExhausted = "structure.id_space_exhausted";
    public const string DuplicateProjectId = "structure.duplicate_project_id";
    public const string InvalidMonument = "structure.invalid_monument";
    public const string InvalidParent = "structure.invalid_parent";
    public const string ParentOutsideBlock = "structure.parent_outside_block";
    public const string ParentAttachmentMismatch = "structure.parent_attachment_mismatch";
    public const string InvalidRootCount = "structure.invalid_root_count";
    public const string ParentCycle = "structure.parent_cycle";
    public const string MissingAlongPlacement = "structure.missing_along_placement";
    public const string DuplicateAlongPlacement = "structure.duplicate_along_placement";
    public const string AlongHostMismatch = "structure.along_host_mismatch";
    public const string AlongPointBeyondCourse = "geometry.along_point_beyond_course";
    public const string PartLengthDiscrepancy = "geometry.part_length_discrepancy";
    public const string ExistingEndMismatch = "geometry.existing_end_mismatch";
}
