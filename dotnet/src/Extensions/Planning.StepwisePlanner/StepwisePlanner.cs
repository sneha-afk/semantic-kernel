﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Diagnostics;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.Planning.Stepwise;
using Microsoft.SemanticKernel.SemanticFunctions;
using Microsoft.SemanticKernel.SkillDefinition;

#pragma warning disable IDE0130
// ReSharper disable once CheckNamespace - Using NS of Plan
namespace Microsoft.SemanticKernel.Planning;
#pragma warning restore IDE0130

/// <summary>
/// A planner that creates a Stepwise plan using Mrkl systems.
/// </summary>
/// <remark>
/// An implementation of a Mrkl system as described in https://arxiv.org/pdf/2205.00445.pdf
/// </remark>
public class StepwisePlanner : IStepwisePlanner
{
    /// <summary>
    /// Initialize a new instance of the <see cref="StepwisePlanner"/> class.
    /// </summary>
    /// <param name="kernel">The semantic kernel instance.</param>
    /// <param name="config">Optional configuration object</param>
    /// <param name="prompt">Optional prompt override</param>
    /// <param name="promptUserConfig">Optional prompt config override</param>
    public StepwisePlanner(
        IKernel kernel,
        StepwisePlannerConfig? config = null,
        string? prompt = null,
        PromptTemplateConfig? promptUserConfig = null)
    {
        Verify.NotNull(kernel);
        this._kernel = kernel;

        this.Config = config ?? new();
        this.Config.ExcludedSkills.Add(RestrictedSkillName);

        var promptConfig = promptUserConfig ?? new PromptTemplateConfig();
        var promptTemplate = prompt ?? EmbeddedResource.Read("Skills.StepwiseStep.skprompt.txt");

        if (promptUserConfig == null)
        {
            string promptConfigString = EmbeddedResource.Read("Skills.StepwiseStep.config.json");
            if (!string.IsNullOrEmpty(promptConfigString))
            {
                promptConfig = PromptTemplateConfig.FromJson(promptConfigString);
            }
        }

        promptConfig.Completion.MaxTokens = this.Config.MaxTokens;

        this._systemStepFunction = this.ImportSemanticFunction(this._kernel, "StepwiseStep", promptTemplate, promptConfig);
        this._nativeFunctions = this._kernel.ImportSkill(this, RestrictedSkillName);

        this._context = this._kernel.CreateNewContext();
        this._logger = this._kernel.Logger;
    }

    /// <inheritdoc />
    public Plan CreatePlan(string goal)
    {
        if (string.IsNullOrEmpty(goal))
        {
            throw new SKException("The goal specified is empty");
        }

        string functionDescriptions = this.GetFunctionDescriptions();

        Plan planStep = new(this._nativeFunctions["ExecutePlan"]);
        planStep.Parameters.Set("functionDescriptions", functionDescriptions);
        planStep.Parameters.Set("question", goal);

        planStep.Outputs.Add("agentScratchPad");
        planStep.Outputs.Add("stepCount");
        planStep.Outputs.Add("skillCount");
        planStep.Outputs.Add("stepsTaken");

        Plan plan = new(goal);

        plan.AddSteps(planStep);

        return plan;
    }

    [SKFunction, SKName("ExecutePlan"), Description("Execute a plan")]
    public async Task<SKContext> ExecutePlanAsync(
        [Description("The question to answer")]
        string question,
        [Description("List of tool descriptions")]
        string functionDescriptions,
        SKContext context)
    {
        var stepsTaken = new List<SystemStep>();
        if (!string.IsNullOrEmpty(question))
        {
            for (int i = 0; i < this.Config.MaxIterations; i++)
            {
                var scratchPad = this.CreateScratchPad(question, stepsTaken);

                context.Variables.Set("agentScratchPad", scratchPad);

                var llmResponse = (await this._systemStepFunction.InvokeAsync(context).ConfigureAwait(false));

                if (llmResponse.ErrorOccurred)
                {
                    throw new SKException($"Error occurred while executing stepwise plan: {llmResponse.LastException?.Message}", llmResponse.LastException);
                }

                string actionText = llmResponse.Result.Trim();
                this._logger?.LogTrace("Response: {ActionText}", actionText);

                var nextStep = this.ParseResult(actionText);
                stepsTaken.Add(nextStep);

                if (!string.IsNullOrEmpty(nextStep.FinalAnswer))
                {
                    this._logger?.LogTrace("Final Answer: {FinalAnswer}", nextStep.FinalAnswer);

                    context.Variables.Update(nextStep.FinalAnswer);
                    var updatedScratchPlan = this.CreateScratchPad(question, stepsTaken);
                    context.Variables.Set("agentScratchPad", updatedScratchPlan);

                    // Add additional results to the context
                    this.AddExecutionStatsToContext(stepsTaken, context);

                    return context;
                }

                this._logger?.LogTrace("Thought: {Thought}", nextStep.Thought);

                if (!string.IsNullOrEmpty(nextStep!.Action!))
                {
                    this._logger?.LogInformation("Action: {Action}. Iteration: {Iteration}.", nextStep.Action, i + 1);
                    this._logger?.LogTrace("Action: {Action}({ActionVariables}). Iteration: {Iteration}.",
                        nextStep.Action, JsonSerializer.Serialize(nextStep.ActionVariables), i + 1);

                    try
                    {
                        await Task.Delay(this.Config.MinIterationTimeMs).ConfigureAwait(false);
                        var result = await this.InvokeActionAsync(nextStep.Action!, nextStep!.ActionVariables!).ConfigureAwait(false);

                        if (string.IsNullOrEmpty(result))
                        {
                            nextStep.Observation = "Got no result from action";
                        }
                        else
                        {
                            nextStep.Observation = result;
                        }
                    }
                    catch (Exception ex) when (!ex.IsCriticalException())
                    {
                        nextStep.Observation = $"Error invoking action {nextStep.Action} : {ex.Message}";
                        this._logger?.LogWarning(ex, "Error invoking action {Action}", nextStep.Action);
                    }

                    this._logger?.LogTrace("Observation: {Observation}", nextStep.Observation);
                }
                else
                {
                    this._logger?.LogInformation("Action: No action to take");
                }

                // sleep 3 seconds
                await Task.Delay(this.Config.MinIterationTimeMs).ConfigureAwait(false);
            }

            context.Variables.Update($"Result not found, review _stepsTaken to see what happened.\n{JsonSerializer.Serialize(stepsTaken)}");
        }
        else
        {
            context.Variables.Update("Question not found.");
        }

        return context;
    }

    public virtual SystemStep ParseResult(string input)
    {
        var result = new SystemStep
        {
            OriginalResponse = input
        };

        // Extract final answer
        Match finalAnswerMatch = s_finalAnswerRegex.Match(input);

        if (finalAnswerMatch.Success)
        {
            result.FinalAnswer = finalAnswerMatch.Groups[1].Value.Trim();
            return result;
        }

        // Extract thought
        Match thoughtMatch = s_thoughtRegex.Match(input);

        if (thoughtMatch.Success)
        {
            result.Thought = thoughtMatch.Value.Trim();
        }
        else if (!input.Contains(Action))
        {
            result.Thought = input;
        }
        else
        {
            throw new InvalidOperationException("Unexpected input format");
        }

        result.Thought = result.Thought.Replace(Thought, string.Empty).Trim();

        // Extract action
        Match actionMatch = s_actionRegex.Match(input);

        if (actionMatch.Success)
        {
            var json = actionMatch.Groups[1].Value.Trim();

            try
            {
                var systemStepResults = JsonSerializer.Deserialize<SystemStep>(json);

                if (systemStepResults == null)
                {
                    result.Observation = $"System step parsing error, empty JSON: {json}";
                }
                else
                {
                    result.Action = systemStepResults.Action;
                    result.ActionVariables = systemStepResults.ActionVariables;
                }
            }
            catch (JsonException)
            {
                result.Observation = $"System step parsing error, invalid JSON: {json}";
            }
        }

        if (string.IsNullOrEmpty(result.Thought) && string.IsNullOrEmpty(result.Action))
        {
            result.Observation = "System step error, no thought or action found. Please give a valid thought and/or action.";
        }

        return result;
    }

    private void AddExecutionStatsToContext(List<SystemStep> stepsTaken, SKContext context)
    {
        context.Variables.Set("stepCount", stepsTaken.Count.ToString(CultureInfo.InvariantCulture));
        context.Variables.Set("stepsTaken", JsonSerializer.Serialize(stepsTaken));

        Dictionary<string, int> actionCounts = new();
        foreach (var step in stepsTaken)
        {
            if (string.IsNullOrEmpty(step.Action)) { continue; }

            _ = actionCounts.TryGetValue(step.Action!, out int currentCount);
            actionCounts[step.Action!] = ++currentCount;
        }

        var skillCallListWithCounts = string.Join(", ", actionCounts.Keys.Select(skill =>
            $"{skill}({actionCounts[skill]})"));

        var skillCallCountStr = actionCounts.Values.Sum().ToString(CultureInfo.InvariantCulture);

        context.Variables.Set("skillCount", $"{skillCallCountStr} ({skillCallListWithCounts})");
    }

    private string CreateScratchPad(string question, List<SystemStep> stepsTaken)
    {
        if (stepsTaken.Count == 0)
        {
            return string.Empty;
        }

        var scratchPadLines = new List<string>();

        // Add the original first thought
        scratchPadLines.Add(ScratchPadPrefix);
        scratchPadLines.Add($"{Thought} {stepsTaken[0].Thought}");

        // Keep track of where to insert the next step
        var insertPoint = scratchPadLines.Count;

        // Keep the most recent steps in the scratch pad.
        for (var i = stepsTaken.Count - 1; i >= 0; i--)
        {
            if (scratchPadLines.Count / 4.0 > (this.Config.MaxTokens * 0.75))
            {
                this._logger.LogDebug("Scratchpad is too long, truncating. Skipping {CountSkipped} steps.", i + 1);
                break;
            }

            var s = stepsTaken[i];

            if (!string.IsNullOrEmpty(s.Observation))
            {
                scratchPadLines.Insert(insertPoint, $"{Observation} {s.Observation}");
            }

            if (!string.IsNullOrEmpty(s.Action))
            {
                scratchPadLines.Insert(insertPoint, $"{Action} {{\"action\": \"{s.Action}\",\"action_variables\": {JsonSerializer.Serialize(s.ActionVariables)}}}");
            }

            if (i != 0)
            {
                scratchPadLines.Insert(insertPoint, $"{Thought} {s.Thought}");
            }
        }

        var scratchPad = string.Join("\n", scratchPadLines).Trim();

        if (!string.IsNullOrWhiteSpace(scratchPad))
        {
            this._logger.LogTrace("Scratchpad: {ScratchPad}", scratchPad);
        }

        return scratchPad;
    }

    private async Task<string> InvokeActionAsync(string actionName, Dictionary<string, string> actionVariables)
    {
        var availableFunctions = this.GetAvailableFunctions();
        var targetFunction = availableFunctions.FirstOrDefault(f => ToFullyQualifiedName(f) == actionName);
        if (targetFunction == null)
        {
            throw new SKException($"The function '{actionName}' was not found.");
        }

        try
        {
            var function = this._kernel.Func(targetFunction.SkillName, targetFunction.Name);
            var actionContext = this.CreateActionContext(actionVariables);

            var result = await function.InvokeAsync(actionContext).ConfigureAwait(false);

            if (result.ErrorOccurred)
            {
                this._logger?.LogError("Error occurred: {Error}", result.LastException);
                return $"Error occurred: {result.LastException}";
            }

            this._logger?.LogTrace("Invoked {FunctionName}. Result: {Result}", targetFunction.Name, result.Result);

            return result.Result;
        }
        catch (Exception e) when (!e.IsCriticalException())
        {
            this._logger?.LogError(e, "Something went wrong in system step: {0}.{1}. Error: {2}", targetFunction.SkillName, targetFunction.Name, e.Message);
            return $"Something went wrong in system step: {targetFunction.SkillName}.{targetFunction.Name}. Error: {e.Message} {e.InnerException.Message}";
        }
    }

    private SKContext CreateActionContext(Dictionary<string, string> actionVariables)
    {
        var actionContext = this._kernel.CreateNewContext();
        if (actionVariables != null)
        {
            foreach (var kvp in actionVariables)
            {
                actionContext.Variables.Set(kvp.Key, kvp.Value);
            }
        }

        return actionContext;
    }

    private IEnumerable<FunctionView> GetAvailableFunctions()
    {
        FunctionsView functionsView = this._context.Skills!.GetFunctionsView();

        var excludedSkills = this.Config.ExcludedSkills ?? new();
        var excludedFunctions = this.Config.ExcludedFunctions ?? new();

        var availableFunctions =
            functionsView.NativeFunctions
                .Concat(functionsView.SemanticFunctions)
                .SelectMany(x => x.Value)
                .Where(s => !excludedSkills.Contains(s.SkillName) && !excludedFunctions.Contains(s.Name))
                .OrderBy(x => x.SkillName)
                .ThenBy(x => x.Name);
        return availableFunctions;
    }

    private string GetFunctionDescriptions()
    {
        var availableFunctions = this.GetAvailableFunctions();

        string functionDescriptions = string.Join("\n", availableFunctions.Select(x => ToManualString(x)));
        return functionDescriptions;
    }

    private ISKFunction ImportSemanticFunction(IKernel kernel, string functionName, string promptTemplate, PromptTemplateConfig config)
    {
        var template = new PromptTemplate(promptTemplate, config, kernel.PromptTemplateEngine);
        var functionConfig = new SemanticFunctionConfig(config, template);

        return kernel.RegisterSemanticFunction(RestrictedSkillName, functionName, functionConfig);
    }

    private static string ToManualString(FunctionView function)
    {
        var inputs = string.Join("\n", function.Parameters.Select(parameter =>
        {
            var defaultValueString = string.IsNullOrEmpty(parameter.DefaultValue) ? string.Empty : $"(default='{parameter.DefaultValue}')";
            return $"  - {parameter.Name}: {parameter.Description} {defaultValueString}";
        }));

        var functionDescription = function.Description.Trim();

        if (string.IsNullOrEmpty(inputs))
        {
            return $"{ToFullyQualifiedName(function)}: {functionDescription}\n";
        }

        return $"{ToFullyQualifiedName(function)}: {functionDescription}\n{inputs}\n";
    }

    private static string ToFullyQualifiedName(FunctionView function)
    {
        return $"{function.SkillName}.{function.Name}";
    }

    /// <summary>
    /// The configuration for the StepwisePlanner
    /// </summary>
    private StepwisePlannerConfig Config { get; }

    // Context used to access the list of functions in the kernel
    private readonly SKContext _context;
    private readonly IKernel _kernel;
    private readonly ILogger _logger;

    /// <summary>
    /// Planner native functions
    /// </summary>
    private IDictionary<string, ISKFunction> _nativeFunctions = new Dictionary<string, ISKFunction>();

    /// <summary>
    /// System step function for Plan execution
    /// </summary>
    private ISKFunction _systemStepFunction;

    /// <summary>
    /// The name to use when creating semantic functions that are restricted from plan creation
    /// </summary>
    private const string RestrictedSkillName = "StepwisePlanner_Excluded";

    /// <summary>
    /// The Action tag
    /// </summary>
    private const string Action = "[ACTION]";

    /// <summary>
    /// The Thought tag
    /// </summary>
    private const string Thought = "[THOUGHT]";

    /// <summary>
    /// The Observation tag
    /// </summary>
    private const string Observation = "[OBSERVATION]";

    /// <summary>
    /// The prefix used for the scratch pad
    /// </summary>
    private const string ScratchPadPrefix = "This was my previous work (but they haven't seen any of it! They only see what I return as final answer):";

    /// <summary>
    /// The regex for parsing the action response
    /// </summary>
    private static readonly Regex s_actionRegex = new(@"\[ACTION\][^{}]*({(?:[^{}]*{[^{}]*})*[^{}]*})", RegexOptions.Singleline);

    /// <summary>
    /// The regex for parsing the thought response
    /// </summary>
    private static readonly Regex s_thoughtRegex = new(@"(\[THOUGHT\])?(?<thought>.+?)(?=\[ACTION\]|$)", RegexOptions.Singleline);

    /// <summary>
    /// The regex for parsing the final answer response
    /// </summary>
    private static readonly Regex s_finalAnswerRegex = new(@"\[FINAL ANSWER\](?<final_answer>.+)", RegexOptions.Singleline);
}
