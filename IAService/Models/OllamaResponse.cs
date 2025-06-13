namespace IAService.Models
{
    public class OllamaResponse
    {
        public string response { get; set; }
    }
    public class OpenAIStyleResponse
    {
        public List<Choice> choices { get; set; }
    }

    public class Choice
    {
        public string text { get; set; }
    }
}
