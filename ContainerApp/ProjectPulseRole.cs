namespace ProjectPulse.Processor.ContainerApp;

public enum ProjectPulseRole
{
    Rta,
    ServiceBus
}

public static class ProjectPulseRoleResolver
{
    public static ProjectPulseRole Resolve()
    {
        var raw = Environment.GetEnvironmentVariable("PROJECT_PULSE_ROLE");
        if (TryResolve(raw, out var role))
        {
            return role;
        }

        throw new InvalidOperationException(
            $"Unsupported PROJECT_PULSE_ROLE '{raw}'. Valid values are RTA or SERVICEBUS.");
    }

    public static bool TryResolve(string? raw, out ProjectPulseRole role)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            role = ProjectPulseRole.Rta;
            return true;
        }

        switch (raw.Trim().ToUpperInvariant())
        {
            case "RTA":
                role = ProjectPulseRole.Rta;
                return true;
            case "SERVICEBUS":
            case "SERVICE_BUS":
                role = ProjectPulseRole.ServiceBus;
                return true;
            default:
                role = ProjectPulseRole.Rta;
                return false;
        }
    }

    public static string ToConfigurationValue(this ProjectPulseRole role) =>
        role switch
        {
            ProjectPulseRole.Rta => "RTA",
            ProjectPulseRole.ServiceBus => "SERVICEBUS",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
        };
}
