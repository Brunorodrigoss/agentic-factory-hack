using System;
using System.IO;
using System.Threading.Tasks;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;
using RepairPlannerAgent.Services;

class Program
{
    static async Task Main(string[] args)
    {
        // Load environment variables from .env file
        LoadEnvironmentVariables("../../.env");

        // Load environment variables
        var azureAiEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_AI_PROJECT_ENDPOINT is required");
        var modelDeploymentName = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME")
            ?? throw new InvalidOperationException("MODEL_DEPLOYMENT_NAME is required");
        var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
            ?? throw new InvalidOperationException("COSMOS_ENDPOINT is required");
        var cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY")
            ?? throw new InvalidOperationException("COSMOS_KEY is required");
        var cosmosDatabaseName = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME")
            ?? throw new InvalidOperationException("COSMOS_DATABASE_NAME is required");

        // Setup dependency injection
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());

        services.AddSingleton<AIProjectClient>(_ => new AIProjectClient(new Uri(azureAiEndpoint), new DefaultAzureCredential()));
        services.AddSingleton<CosmosDbService>(sp => new CosmosDbService(
            cosmosEndpoint,
            cosmosKey,
            cosmosDatabaseName,
            sp.GetRequiredService<ILogger<CosmosDbService>>()));
        services.AddSingleton<IFaultMappingService, FaultMappingService>();
        services.AddSingleton<global::RepairPlannerAgent.RepairPlannerAgent>(sp => new global::RepairPlannerAgent.RepairPlannerAgent(
            sp.GetRequiredService<AIProjectClient>(),
            sp.GetRequiredService<CosmosDbService>(),
            sp.GetRequiredService<IFaultMappingService>(),
            modelDeploymentName,
            sp.GetRequiredService<ILogger<global::RepairPlannerAgent.RepairPlannerAgent>>()));

        await using var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<Program>>();
        var agent = provider.GetRequiredService<global::RepairPlannerAgent.RepairPlannerAgent>();

        try
        {
            // Ensure agent is registered
            await agent.EnsureAgentVersionAsync();

            // Create a sample diagnosed fault
            var fault = new DiagnosedFault
            {
                Id = Guid.NewGuid().ToString(),
                MachineId = "machine-001",
                FaultType = "curing_temperature_excessive",
                Description = "Curing temperature exceeded safe threshold by 15°C",
                Timestamp = DateTime.Now,
                Severity = "high"
            };

            logger.LogInformation("Planning repair for machine {MachineId}, fault={FaultType}", fault.MachineId, fault.FaultType);

            // Run the repair planning workflow
            var workOrder = await agent.PlanAndCreateWorkOrderAsync(fault);

            logger.LogInformation("Saved work order {WorkOrderNumber} (id={Id}, status={Status}, assignedTo={AssignedTo})",
                workOrder.WorkOrderNumber, workOrder.Id, workOrder.Status, workOrder.AssignedTo);

            // Output the work order as JSON
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(workOrder, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during repair planning");
            throw;
        }
    }

    static void LoadEnvironmentVariables(string envFilePath)
    {
        if (!File.Exists(envFilePath))
        {
            throw new InvalidOperationException($".env file not found at {envFilePath}");
        }

        foreach (var line in File.ReadAllLines(envFilePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
            var parts = line.Split('=', 2);
            if (parts.Length == 2)
            {
                var key = parts[0].Trim();
                var value = parts[1].Trim();
                if (value.StartsWith('"') && value.EndsWith('"'))
                {
                    value = value.Substring(1, value.Length - 2);
                }
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
