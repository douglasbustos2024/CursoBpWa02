namespace Bdb.Curso.Application.Shared.Dtos
{
    public class GetProductRequest
    {
        public string searchTerm { get; set; } = string.Empty;
        public int pageNumber { get; set; }

    }
}
