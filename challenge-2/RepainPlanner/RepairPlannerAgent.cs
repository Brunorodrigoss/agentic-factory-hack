using System.Text.Json;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;
using RepairPlannerAgent.Services;
using System.Threading;
using System.Threading.Tasks;

namespace RepairPlannerAgent
{
    public sealed class RepairPlannerAgent(
        AIProjectClient projectClient,
        CosmosDbService cosmosDb,
        IFaultMappingService faultMapping,
        string modelDeploymentName,
        ILogger<RepairPlannerAgent> logger)
    {
        private const string AgentName = "RepairPlannerAgent";
        private const string AgentInstructions = """
            You are a Repair Planner Agent for tire manufacturing equipment.
            Generate a repair plan with tasks, timeline, and resource allocation.
            Return the response as valid JSON matching the WorkOrder schema.
            
            Output JSON with these fields:
            - workOrderNumber, machineId, title, description
            - type: "corrective" | "preventive" | "emergency"
            - priority: "critical" | "high" | "medium" | "low"
            - status, assignedTo (technician id or null), notes
            - estimatedDuration: integer (minutes, e.g. 60 not "60 minutes")
            - partsUsed: [{ partId, partNumber, quantity }]
            - tasks: [{ sequence, title, description, estimatedDurationMinutes (integer), requiredSkills, safetyNotes }]
            
            IMPORTANT: All duration fields must be integers representing minutes (e.g. 90), not strings.
            
            Rules:
            - Assign the most qualified available technician
            - Include only relevant parts; empty array if none needed
            - Tasks must be ordered and actionable
            """;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
        {
            var definition = new PromptAgentDefinition(model: modelDeploymentName) { Instructions = AgentInstructions };
            await projectClient.Agents.CreateAgentVersionAsync(AgentName, new AgentVersionCreationOptions(definition), ct);
        }

        public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(DiagnosedFault fault, CancellationToken ct = default)
        {
            // 1. Get required skills and parts from mapping
            var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
            var requiredParts = faultMapping.GetRequiredParts(fault.FaultType);

            // 2. Query technicians and parts from Cosmos DB
            var availableTechnicians = await cosmosDb.GetAvailableTechniciansWithSkillsAsync(requiredSkills.ToList(), ct);
            var availableParts = await cosmosDb.GetPartsInventoryAsync(requiredParts.ToList(), ct);

            // 3. Build prompt and invoke agent
            var prompt = BuildPrompt(fault, availableTechnicians, availableParts);
            var agent = projectClient.GetAIAgent(name: AgentName);
            var response = await agent.RunAsync(prompt, thread: null, options: null, cancellationToken: ct);
            var resultText = response.Text?.Trim() ?? "{}";

            // Clean the response if it's wrapped in code blocks
            if (resultText.StartsWith("```") && resultText.EndsWith("```"))
            {
                resultText = resultText.Substring(3, resultText.Length - 6).Trim();
                if (resultText.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                {
                    resultText = resultText.Substring(4).Trim();
                }
            }

            // 4. Parse response and apply defaults
            WorkOrder workOrder;
            try
            {
                workOrder = JsonSerializer.Deserialize<WorkOrder>(resultText, JsonOptions) ?? new WorkOrder();
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Failed to parse agent response as WorkOrder JSON");
                throw;
            }

            // Apply defaults
            if (string.IsNullOrEmpty(workOrder.Id))
            {
                workOrder.Id = Guid.NewGuid().ToString();
            }
            if (string.IsNullOrEmpty(workOrder.WorkOrderNumber))
            {
                workOrder.WorkOrderNumber = $"WO-{DateTime.Now:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}";
            }
            if (string.IsNullOrEmpty(workOrder.MachineId))
            {
                workOrder.MachineId = fault.MachineId;
            }
            if (string.IsNullOrEmpty(workOrder.Status))
            {
                workOrder.Status = "new";
            }
            if (string.IsNullOrEmpty(workOrder.Priority))
            {
                workOrder.Priority = "medium";
            }
            if (string.IsNullOrEmpty(workOrder.Type))
            {
                workOrder.Type = "corrective";
            }
            if (string.IsNullOrEmpty(workOrder.AssignedTo))
            {
                workOrder.AssignedTo = availableTechnicians.FirstOrDefault()?.Id;
            }

            // 5. Save to Cosmos DB
            await cosmosDb.CreateWorkOrderAsync(workOrder, ct);

            return workOrder;
        }

        private static string BuildPrompt(DiagnosedFault fault, List<Technician> technicians, List<Part> parts)
        {
            var techList = string.Join("\n", technicians.Select(t => $"- ID: {t.Id}, Name: {t.Name}, Skills: {string.Join(", ", t.Skills)}"));
            var partsList = string.Join("\n", parts.Select(p => $"- PartNumber: {p.PartNumber}, Description: {p.Description}"));

            return $"""
                Diagnosed Fault:
                - Machine ID: {fault.MachineId}
                - Fault Type: {fault.FaultType}
                - Description: {fault.Description}
                - Severity: {fault.Severity}

                Available Technicians:
                {techList}

                Available Parts:
                {partsList}

                Generate a complete repair work order in JSON format.
                """;
        }
    }
}