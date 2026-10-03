namespace Deed.Core;

public static class ProjectIdAllocator
{
    public static DomainResult<ProjectIdAllocation> Allocate(DeedProject project, ProjectIdKind kind)
    {
        var invalid = ProjectValidator.Validate(project).FirstOrDefault(d =>
            d.Code is DiagnosticCodes.IdCounterBehind or DiagnosticCodes.DuplicateProjectId or
                DiagnosticCodes.InvalidId or DiagnosticCodes.IdMismatch);
        if (invalid is not null)
            return DomainResult<ProjectIdAllocation>.Failure(invalid.Code, invalid.Message);
        return project.IdCounters.Allocate(kind);
    }
}
