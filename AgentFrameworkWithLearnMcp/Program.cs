using System.Threading.Tasks;

class Program
{
    // Minimal entry point delegating to the app runner
    static async Task Main(string[] args)
    {
        await new AgentApp().RunAsync(args);
    }
}
