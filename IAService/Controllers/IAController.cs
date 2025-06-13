using IAService.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text;
using System.Data.SqlClient;
using System.Text.RegularExpressions;
using IAService.Models;


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

            // Interpretamos la intención de la pregunta
            var (tipoConsulta, parametro) = await InterpretarPregunta(pregunta);

            if (string.IsNullOrWhiteSpace(tipoConsulta) || string.IsNullOrWhiteSpace(parametro))
            {
                // Considera ser más específico o usar un prompt para Ollama aquí si quieres una respuesta más amable
                return BadRequest("No se pudo interpretar correctamente la pregunta. Por favor, intenta reformularla.");
            }

            // Consultamos en la base de datos
            var resultadoConsulta = await ConsultarVistaHorarios(tipoConsulta, parametro);

            // Armamos el prompt para Ollama utilizando la nueva clase
            string prompt;
            if (resultadoConsulta.Any())
            {
                prompt = IAPromptFactory.ObtenerPromptConResultados(pregunta, resultadoConsulta);
            }
            else
            {
                prompt = IAPromptFactory.ObtenerPromptSinResultados(pregunta);
            }

            var respuesta = await ConsultarOllama(prompt);
            return Ok(new { respuesta });
        }

        private async Task<(string TipoConsulta, string Parametro)> InterpretarPregunta(string pregunta)
        {
            try
            {
                var prompt = IAPromptFactory.ObtenerPromptInterpretacion(pregunta);

                var json = await GenerarRespuestaAsync(prompt);

                var parsed = JsonSerializer.Deserialize<OllamaResponse>(json);

                // El campo response viene como string JSON, así que hay que parsearlo nuevamente
                using var doc = JsonDocument.Parse(parsed?.response ?? "{}");
                var tipo = doc.RootElement.GetProperty("tipo").GetString() ?? "";
                var valor = doc.RootElement.GetProperty("valor").GetString() ?? "";

                return (tipo.ToLower(), valor);
            }
            catch (JsonException jsonEx)
            {
                // Error al parsear el JSON
                Console.WriteLine($"Error de deserialización: {jsonEx.Message}");
                return ("", "");
            }
            catch (Exception ex)
            {
                // Otro error inesperado
                Console.WriteLine($"Error inesperado: {ex.Message}");
                return ("", "");
            }
        }

        //private async Task<(string TipoConsulta, string Parametro)> InterpretarPregunta(string pregunta)
        //{
        //    var prompt = IAPromptFactory.ObtenerPromptInterpretacion(pregunta);

        //    var json = await GenerarRespuestaAsync(prompt);

        //    var parsed = JsonSerializer.Deserialize<OllamaResponse>(json);

        //    // El campo response viene como string JSON, así que hay que parsearlo nuevamente
        //    using var doc = JsonDocument.Parse(parsed?.response ?? "{}");
        //    var tipo = doc.RootElement.GetProperty("tipo").GetString() ?? "";
        //    var valor = doc.RootElement.GetProperty("valor").GetString() ?? "";

        //    return (tipo.ToLower(), valor);
        //    //var parsed = JsonSerializer.Deserialize<OpenAIStyleResponse>(json);
        //    //var rawText = parsed?.choices?.FirstOrDefault()?.text ?? "";
        //    //var match = Regex.Match(rawText, @"\{[^{}]*(""[^""]*""\s*:\s*(""[^""]*""|\d+)[^{}]*)*\}");

        //    //if (!match.Success)
        //    //    return ("", "");

        //    //var jsonClean = match.Value;

        //    //using var doc = JsonDocument.Parse(jsonClean);
        //    //var tipo = doc.RootElement.GetProperty("tipo").GetString() ?? "";
        //    //var valor = doc.RootElement.GetProperty("valor").GetString() ?? "";

        //    //return (tipo.ToLower(), valor);
        //}


        private async Task<string> GenerarRespuestaAsync(string prompt)
        {
            try
            {
                var payload = new
                {
                    model = "llama3:instruct",
                    //model = "gemma",
                    prompt = prompt,
                    stream = false
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                //var response = await _httpClient.PostAsync("http://172.30.57.196:8081/v1/completions", content);
                var response = await _httpClient.PostAsync("http://localhost:11434/api/generate", content);

                response.EnsureSuccessStatusCode(); 

                var jsonResponse = await response.Content.ReadAsStringAsync();

                return jsonResponse;
            }
            catch (HttpRequestException httpEx)
            {
                // Error de red, conexión rechazada, timeout, etc.
                return $"Error de red: {httpEx.Message}";
            }
            catch (TaskCanceledException timeoutEx)
            {
                // Timeout
                return $"Timeout al esperar la respuesta: {timeoutEx.Message}";
            }
            catch (Exception ex)
            {
                // Cualquier otro error inesperado
                return $"Error inesperado: {ex.Message}";
            }
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
            SELECT TOP 2 Dia,SalonNombre,BloqueNombre,EdificioNombre,SedeNombre,AsignaturaNombre, DocenteNombre
            FROM vw_HorariosConDetalle
            WHERE {columna} LIKE '%' + @parametro + '%'";

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@parametro", parametro);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var linea = $"Día: {reader["Dia"]}, Asignatura: {reader["AsignaturaNombre"]}, BloqueNombre: {reader["BloqueNombre"]}, Docente: {reader["DocenteNombre"]}, Salón: {reader["SalonNombre"]}, Edificio: {reader["EdificioNombre"]}";
                resultados.Add(linea);
            }

            return resultados;
        }

        private async Task<string> ConsultarOllama(string prompt)
        {
            var json = await GenerarRespuestaAsync(prompt);

            //var parsed = JsonSerializer.Deserialize<OllamaResponse>(json);
            var parsed = JsonSerializer.Deserialize<OllamaResponse>(json);

            return parsed?.response ?? "No se pudo generar una respuesta.";
            //var parsed = JsonSerializer.Deserialize<OpenAIStyleResponse>(json);
            //return parsed?.choices?.FirstOrDefault()?.text ?? "No se pudo generar una respuesta.";

        }
    }
}
