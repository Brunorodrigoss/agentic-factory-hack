using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;

namespace RepairPlannerAgent.Services
{
    public sealed class CosmosDbService
    {
        private readonly CosmosClient _client;
        private readonly Container _techniciansContainer;
        private readonly Container _partsContainer;
        private readonly Container _workOrdersContainer;
        private readonly ILogger<CosmosDbService> _logger;

        public CosmosDbService(string endpoint, string key, string databaseName, ILogger<CosmosDbService> logger)
        {
            _client = new CosmosClient(endpoint, key);
            var database = _client.GetDatabase(databaseName);
            
            _techniciansContainer = database.GetContainer("Technicians");
            _partsContainer = database.GetContainer("PartsInventory");
            _workOrdersContainer = database.GetContainer("WorkOrders");
            _logger = logger;
        }

        public async Task<List<Technician>> GetAvailableTechniciansWithSkillsAsync(List<string> requiredSkills, CancellationToken ct = default)
        {
            try
            {
                _logger.LogInformation("Querying available technicians with skills: {Skills}", string.Join(", ", requiredSkills));
                var query = "SELECT * FROM c WHERE c.isAvailable = true";
                var queryDefinition = new QueryDefinition(query);
                var iterator = _techniciansContainer.GetItemQueryIterator<Technician>(queryDefinition);
                var technicians = new List<Technician>();
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync(ct);
                    technicians.AddRange(response);
                }
                // Filter in memory for technicians who have at least one required skill
                var matchingTechnicians = technicians.Where(t => t.Skills.Any(s => requiredSkills.Contains(s, StringComparer.OrdinalIgnoreCase))).ToList();
                _logger.LogInformation("Found {Count} available technicians matching skills", matchingTechnicians.Count);
                return matchingTechnicians;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying technicians");
                throw;
            }
        }

        public async Task<List<Part>> GetPartsInventoryAsync(List<string> partNumbers, CancellationToken ct = default)
        {
            try
            {
                _logger.LogInformation("Fetching parts: {Parts}", string.Join(", ", partNumbers));
                if (partNumbers.Count == 0)
                {
                    return new List<Part>();
                }
                var parameters = partNumbers.Select((p, i) => new { Name = $"@p{i}", Value = p }).ToList();
                var inClause = string.Join(",", parameters.Select(p => p.Name));
                var query = $"SELECT * FROM c WHERE c.partNumber IN ({inClause})";
                var queryDefinition = new QueryDefinition(query);
                foreach (var param in parameters)
                {
                    queryDefinition.WithParameter(param.Name, param.Value);
                }
                var iterator = _partsContainer.GetItemQueryIterator<Part>(queryDefinition);
                var parts = new List<Part>();
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync(ct);
                    parts.AddRange(response);
                }
                _logger.LogInformation("Fetched {Count} parts", parts.Count);
                return parts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching parts");
                throw;
            }
        }

        public async Task<string> CreateWorkOrderAsync(WorkOrder workOrder, CancellationToken ct = default)
        {
            try
            {
                _logger.LogInformation("Creating work order {Number}", workOrder.WorkOrderNumber);
                var response = await _workOrdersContainer.CreateItemAsync(workOrder, new PartitionKey(workOrder.Status), cancellationToken: ct);
                _logger.LogInformation("Created work order with id {Id}", response.Resource.Id);
                return response.Resource.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating work order");
                throw;
            }
        }
    }
}