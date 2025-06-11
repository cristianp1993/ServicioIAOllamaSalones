using IAService.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text;
using System.Data.SqlClient;

namespace IAService.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class IAController : Controller
    {
        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient;

        public IAController(IConfiguration config)
        {
            _config = config;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };

        }

        [HttpPost("preguntar")]
        public async Task<IActionResult> Preguntar([FromBody] PreguntaRequest request)
        {
            var pregunta = request.Pregunta;

            // interpretamos la intención de la pregunta
            var (tipoConsulta, parametro) = await InterpretarPregunta(pregunta);

            if (string.IsNullOrWhiteSpace(tipoConsulta) || string.IsNullOrWhiteSpace(parametro))
                return BadRequest("No se pudo interpretar correctamente la pregunta.");

            //Consultamos en la base de datos
            var resultadoConsulta = await ConsultarVistaHorarios(tipoConsulta, parametro);

            //Armamos el prompt para Ollama con los datos encontrados
            var prompt = resultadoConsulta.Any()
            ? $"""
            Responde de forma amable, clara y breve a la siguiente pregunta del usuario:

            Pregunta: {pregunta}

            Datos disponibles:
            {string.Join("\n", resultadoConsulta)}

            Si en los datos hay un cambio de salón (ver campos 'CambioMotivo' y 'NuevoSalonNombre'), menciona que el salón fue cambiado, indica el nuevo salón y el motivo.

            Si no hay cambio (es decir, esos campos están vacíos), responde directamente a la pregunta sin dar explicaciones innecesarias.

            No te disculpes. Sé empático pero breve y directo.
            """
                : $"No se encontraron resultados para la pregunta: '{pregunta}'. Responde de manera amable indicando que no hay datos disponibles.";

            var respuesta = await ConsultarOllama(prompt);
            return Ok(new { respuesta });
        }

        private async Task<(string TipoConsulta, string Parametro)> InterpretarPregunta(string pregunta)
        {
            var prompt = $@"
            Dado el siguiente texto de una pregunta, extrae qué tipo de búsqueda debe hacerse 
            ('docente', 'salon', 'asignatura', 'cambio_salon') y cuál es el valor que se debe usar para buscar. 

            Reglas:
            - Usa 'docente' para términos como 'profesor', 'maestro', etc.
            - Usa 'asignatura' para términos como 'clase', 'materia'.
            - Usa 'salon' para cualquier mención de un salón o aula.
            - Usa 'cambio_salon' si la pregunta está relacionada con a qué salón fue cambiada una clase o asignatura, 
              o cuál fue el motivo del cambio.

            Devuelve un JSON con 'tipo' y 'valor'. Ejemplos:

            Pregunta: ¿Dónde estará el jueves el profesor Robert?
            Respuesta: {{ ""tipo"": ""docente"", ""valor"": ""Robert"" }}

            Pregunta: ¿En qué salón se dictará Gestión Empresarial y qué profesor es el encargado?
            Respuesta: {{ ""tipo"": ""asignatura"", ""valor"": ""Gestión Empresarial"" }}

            Pregunta: ¿Dónde está el salón G402B?
            Respuesta: {{ ""tipo"": ""salon"", ""valor"": ""G402B"" }}

            Pregunta: ¿A qué hora es la clase de Álgebra?
            Respuesta: {{ ""tipo"": ""asignatura"", ""valor"": ""Álgebra"" }}

            Pregunta: ¿A qué salón se cambió la clase de Matemáticas?
            Respuesta: {{ ""tipo"": ""cambio_salon"", ""valor"": ""Matemáticas"" }}

            Pregunta: ¿Por qué motivo se cambió la clase de Física?
            Respuesta: {{ ""tipo"": ""cambio_salon"", ""valor"": ""Física"" }}

            Ahora analiza esta pregunta:
            {pregunta}

            Devuélvelo solo en JSON sin explicaciones.
            ";



            var payload = new
            {
                model = "llama3",
                prompt = prompt,
                stream = false
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("http://localhost:11434/api/generate", content);
            var json = await response.Content.ReadAsStringAsync();

            var parsed = JsonSerializer.Deserialize<OllamaResponse>(json);

            // El campo response viene como string JSON, así que hay que parsearlo nuevamente
            using var doc = JsonDocument.Parse(parsed?.response ?? "{}");
            var tipo = doc.RootElement.GetProperty("tipo").GetString() ?? "";
            var valor = doc.RootElement.GetProperty("valor").GetString() ?? "";

            return (tipo.ToLower(), valor);
        }

        private async Task<List<string>> ConsultarVistaHorarios(string tipo, string valor)
        {
            var resultados = new List<string>();
            var connectionString = _config.GetConnectionString("DefaultConnection");

            var (columna, parametro) = tipo switch
            {
                "docente" => ("DocenteNombre", valor),
                "asignatura" => ("AsignaturaNombre", valor),
                "salon" => ("SalonNombre", valor),
                _ => (null, null)
            };

            if (columna == null)
                return resultados;

            var query = $@"
            SELECT TOP 5 *
            FROM vw_HorariosConDetalle
            WHERE {columna} LIKE '%' + @parametro + '%'";

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@parametro", parametro);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var linea = $"Día: {reader["Dia"]}, Hora: {reader["HoraInicio"]} - {reader["HoraFin"]}, Asignatura: {reader["AsignaturaNombre"]}, Docente: {reader["DocenteNombre"]}, Salón: {reader["SalonNombre"]}, Edificio: {reader["EdificioNombre"]}";
                resultados.Add(linea);
            }

            return resultados;
        }

        private async Task<string> ConsultarOllama(string prompt)
        {
            var payload = new
            {
                model = "llama3",
                prompt = prompt,
                stream = false
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("http://localhost:11434/api/generate", content);
            var json = await response.Content.ReadAsStringAsync();

            var parsed = JsonSerializer.Deserialize<OllamaResponse>(json);
            return parsed?.response ?? "No se pudo generar una respuesta.";
        }
    }
}
