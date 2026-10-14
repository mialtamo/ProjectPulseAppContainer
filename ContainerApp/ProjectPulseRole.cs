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

        if (string.IsNullOrWhiteSpace(raw))
        {
            return ProjectPulseRole.Rta;
        }

        return raw.Trim().ToUpperInvariant() switch
        {
            "RTA" => ProjectPulseRole.Rta,
            "SERVICEBUS" => ProjectPulseRole.ServiceBus,
            "SERVICE_BUS" => ProjectPulseRole.ServiceBus,
            _ => throw new InvalidOperationException(
                $"Unsupported PROJECT_PULSE_ROLE '{raw}'. Valid values are RTA or SERVICEBUS.")
        };
    }

    public static string ToConfigurationValue(this ProjectPulseRole role) =>
        role switch
        {
            ProjectPulseRole.Rta => "RTA",
            ProjectPulseRole.ServiceBus => "SERVICEBUS",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
        };
}
