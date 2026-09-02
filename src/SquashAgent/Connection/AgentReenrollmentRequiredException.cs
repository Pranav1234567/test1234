namespace SquashAgent.Connection;

public sealed class AgentReenrollmentRequiredException : Exception
{
    public AgentReenrollmentRequiredException()
        : base("The Control Plane rejected the device credential; re-enrollment is required.")
    {
    }
}