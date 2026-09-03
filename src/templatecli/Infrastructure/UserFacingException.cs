namespace TemplateCli.Infrastructure;

public sealed class UserFacingException : Exception
{
    public UserFacingException(string message)
        : base(message)
    {
    }

    public UserFacingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
