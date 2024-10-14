namespace Bdb.Curso.Application.Shared.Dtos
{
    public class LoginModel
    {
        public string UserName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ClientType { get; set; } = string.Empty; // "web", "mobile", "api", etc.
                                                 
    }
}
