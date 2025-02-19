﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Reflection;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Services;
using WorkflowCore.Exceptions;
using WorkflowCore.Interface;
using WorkflowCore.Models;
using WorkflowCore.Models.DefinitionStorage.v1;
using WorkflowCore.Primitives;

namespace LINGYUN.Abp.WorkflowManagement
{
    public class WorkflowManager : DomainService, ITransientDependency
    {
        private readonly IWorkflowRegistry _registry;

        public WorkflowManager(IWorkflowRegistry registry)
        {
            _registry = registry;
        }

        public virtual bool IsRegistered(Workflow workflow)
        {
            return _registry.IsRegistered(workflow.Id.ToString(), workflow.Version);
        }

        public virtual void UnRegister(Workflow workflow)
        {
            if (IsRegistered(workflow))
            {
                _registry.DeregisterWorkflow(workflow.Id.ToString(), workflow.Version);
            }
        }

        public virtual WorkflowDefinition Register(
            Workflow workflow,
            ICollection<StepNode> steps,
            ICollection<CompensateNode> compensates)
        {
            if (!IsRegistered(workflow))
            {
                var dataType = typeof(Dictionary<string, object>);
                var source = new DefinitionSourceV1
                {
                    Id = workflow.Id.ToString(),
                    Version = workflow.Version,
                    Description = workflow.Description ?? workflow.DisplayName,
                    DataType = $"{dataType.FullName}, {dataType.Assembly}",
                    DefaultErrorBehavior = workflow.ErrorBehavior,
                    DefaultErrorRetryInterval = workflow.ErrorRetryInterval,
                    Steps = ConvertSteps(steps, compensates)
                };

                var def = Convert(source);

                _registry.RegisterWorkflow(def);

                return def;
            }

            return _registry.GetDefinition(workflow.Id.ToString(), workflow.Version);
        }

        private List<StepSourceV1> ConvertSteps(
            ICollection<StepNode> steps,
            ICollection<CompensateNode> compensates)
        {
            var nodes = new List<StepSourceV1>();

            foreach (var step in steps)
            {
                var source = new StepSourceV1
                {
                    Id = step.Id.ToString(),
                    Saga = step.Saga,
                    StepType = step.StepType,
                    CancelCondition = step.CancelCondition,
                    ErrorBehavior = step.ErrorBehavior,
                    RetryInterval = step.RetryInterval,
                    Name = step.Name
                };

                foreach (var input in step.Inputs)
                {
                    source.Inputs.AddIfNotContains(input);
                }

                foreach (var output in step.Outputs)
                {
                    source.Outputs.Add(output.Key, output.Value.ToString());
                }

                foreach (var nextStep in step.SelectNextStep)
                {
                    source.SelectNextStep.Add(nextStep.Key, nextStep.Value.ToString());
                }

                var childrenNodes = steps.Where(x => Equals(x.ParentId, step.Id)).ToArray();
                if (childrenNodes.Any())
                {
                    source.NextStepId = childrenNodes[0].Id.ToString();

                    nodes.AddRange(ConvertSteps(childrenNodes, compensates));
                }

                var stepCps = compensates.Where(x => Equals(x.ParentId, step.Id)).ToArray();
                if (stepCps.Any())
                {
                    source.CompensateWith.AddRange(ConvertCompensateSteps(stepCps));
                }

                nodes.Add(source);
            }

            return nodes;
        }

        private List<StepSourceV1> ConvertCompensateSteps(
            ICollection<CompensateNode> compensates)
        {
            var nodes = new List<StepSourceV1>();

            foreach (var step in compensates)
            {
                var source = new StepSourceV1
                {
                    Id = step.Id.ToString(),
                    Saga = step.Saga,
                    StepType = step.StepType,
                    CancelCondition = step.CancelCondition,
                    ErrorBehavior = step.ErrorBehavior,
                    RetryInterval = step.RetryInterval,
                    Name = step.Name
                };

                foreach (var input in step.Inputs)
                {
                    source.Inputs.AddIfNotContains(input);
                }

                foreach (var output in step.Outputs)
                {
                    source.Outputs.Add(output.Key, output.Value.ToString());
                }

                foreach (var nextStep in step.SelectNextStep)
                {
                    source.SelectNextStep.Add(nextStep.Key, nextStep.Value.ToString());
                }

                var stepCps = compensates.Where(x => Equals(x.ParentId, step.Id)).ToArray();
                if (stepCps.Any())
                {
                    source.CompensateWith.AddRange(ConvertCompensateSteps(stepCps));
                }

                nodes.Add(source);
            }

            return nodes;
        }

        private WorkflowDefinition Convert(DefinitionSourceV1 source)
        {
            var dataType = typeof(object);
            if (!string.IsNullOrEmpty(source.DataType))
                dataType = FindType(source.DataType);

            var result = new WorkflowDefinition
            {
                Id = source.Id,
                Version = source.Version,
                Steps = ConvertSteps(source.Steps, dataType),
                DefaultErrorBehavior = source.DefaultErrorBehavior,
                DefaultErrorRetryInterval = source.DefaultErrorRetryInterval,
                Description = source.Description,
                DataType = dataType
            };

            return result;
        }


        private WorkflowStepCollection ConvertSteps(ICollection<StepSourceV1> source, Type dataType)
        {
            var result = new WorkflowStepCollection();
            int i = 0;
            var stack = new Stack<StepSourceV1>(source.Reverse<StepSourceV1>());
            var parents = new List<StepSourceV1>();
            var compensatables = new List<StepSourceV1>();

            while (stack.Count > 0)
            {
                var nextStep = stack.Pop();

                var stepType = FindType(nextStep.StepType);

                WorkflowStep targetStep;

                Type containerType;
                if (stepType.GetInterfaces().Contains(typeof(IStepBody)))
                {
                    containerType = typeof(WorkflowStep<>).MakeGenericType(stepType);

                    targetStep = (containerType.GetConstructor(new Type[] { }).Invoke(null) as WorkflowStep);
                }
                else
                {
                    targetStep = stepType.GetConstructor(new Type[] { }).Invoke(null) as WorkflowStep;
                    if (targetStep != null)
                        stepType = targetStep.BodyType;
                }

                if (nextStep.Saga)
                {
                    containerType = typeof(SagaContainer<>).MakeGenericType(stepType);
                    targetStep = (containerType.GetConstructor(new Type[] { }).Invoke(null) as WorkflowStep);
                }

                if (!string.IsNullOrEmpty(nextStep.CancelCondition))
                {
                    var cancelExprType = typeof(Expression<>).MakeGenericType(typeof(Func<,>).MakeGenericType(dataType, typeof(bool)));
                    var dataParameter = Expression.Parameter(dataType, "data");
                    var cancelExpr = DynamicExpressionParser.ParseLambda(new[] { dataParameter }, typeof(bool), nextStep.CancelCondition);
                    targetStep.CancelCondition = cancelExpr;
                }

                targetStep.Id = i;
                targetStep.Name = nextStep.Name;
                targetStep.ErrorBehavior = nextStep.ErrorBehavior;
                targetStep.RetryInterval = nextStep.RetryInterval;
                targetStep.ExternalId = $"{nextStep.Id}";

                AttachInputs(nextStep, dataType, stepType, targetStep);
                AttachOutputs(nextStep, dataType, stepType, targetStep);

                if (nextStep.Do != null)
                {
                    foreach (var branch in nextStep.Do)
                    {
                        foreach (var child in branch.Reverse<StepSourceV1>())
                            stack.Push(child);
                    }

                    if (nextStep.Do.Count > 0)
                        parents.Add(nextStep);
                }

                if (nextStep.CompensateWith != null)
                {
                    foreach (var compChild in nextStep.CompensateWith.Reverse<StepSourceV1>())
                        stack.Push(compChild);

                    if (nextStep.CompensateWith.Count > 0)
                        compensatables.Add(nextStep);
                }

                AttachOutcomes(nextStep, dataType, targetStep);

                result.Add(targetStep);

                i++;
            }

            foreach (var step in result)
            {
                if (result.Any(x => x.ExternalId == step.ExternalId && x.Id != step.Id))
                    throw new WorkflowDefinitionLoadException($"Duplicate step Id {step.ExternalId}");

                foreach (var outcome in step.Outcomes)
                {
                    if (result.All(x => x.ExternalId != outcome.ExternalNextStepId))
                        throw new WorkflowDefinitionLoadException($"Cannot find step id {outcome.ExternalNextStepId}");

                    outcome.NextStep = result.Single(x => x.ExternalId == outcome.ExternalNextStepId).Id;
                }
            }

            foreach (var parent in parents)
            {
                var target = result.Single(x => x.ExternalId == parent.Id);
                foreach (var branch in parent.Do)
                {
                    var childTags = branch.Select(x => x.Id).ToList();
                    target.Children.AddRange(result
                        .Where(x => childTags.Contains(x.ExternalId))
                        .OrderBy(x => x.Id)
                        .Select(x => x.Id)
                        .Take(1)
                        .ToList());
                }
            }

            foreach (var item in compensatables)
            {
                var target = result.Single(x => x.ExternalId == item.Id);
                var tag = item.CompensateWith.Select(x => x.Id).FirstOrDefault();
                if (tag != null)
                {
                    var compStep = result.FirstOrDefault(x => x.ExternalId == tag);
                    if (compStep != null)
                        target.CompensationStepId = compStep.Id;
                }
            }

            return result;
        }

        private void AttachInputs(StepSourceV1 source, Type dataType, Type stepType, WorkflowStep step)
        {
            foreach (var input in source.Inputs)
            {
                var dataParameter = Expression.Parameter(dataType, "data");
                var contextParameter = Expression.Parameter(typeof(IStepExecutionContext), "context");
                var environmentVarsParameter = Expression.Parameter(typeof(IDictionary), "environment");
                var stepProperty = stepType.GetProperty(input.Key);

                if (stepProperty == null)
                {
                    throw new ArgumentException($"Unknown property for input {input.Key} on {source.Id}");
                }

                if (input.Value is string)
                {
                    var acn = BuildScalarInputAction(input, dataParameter, contextParameter, environmentVarsParameter, stepProperty);
                    step.Inputs.Add(new ActionParameter<IStepBody, object>(acn));
                    continue;
                }

                if ((input.Value is IDictionary<string, object>) || (input.Value is IDictionary<object, object>))
                {
                    var acn = BuildObjectInputAction(input, dataParameter, contextParameter, environmentVarsParameter, stepProperty);
                    step.Inputs.Add(new ActionParameter<IStepBody, object>(acn));
                    continue;
                }

                throw new ArgumentException($"Unknown type for input {input.Key} on {source.Id}");
            }
        }

        private void AttachOutputs(StepSourceV1 source, Type dataType, Type stepType, WorkflowStep step)
        {
            foreach (var output in source.Outputs)
            {
                var stepParameter = Expression.Parameter(stepType, "step");
                var sourceExpr = DynamicExpressionParser.ParseLambda(new[] { stepParameter }, typeof(object), output.Value);

                var dataParameter = Expression.Parameter(dataType, "data");


                if (output.Key.Contains(".") || output.Key.Contains("["))
                {
                    AttachNestedOutput(output, step, source, sourceExpr, dataParameter);
                }
                else
                {
                    AttachDirectlyOutput(output, step, dataType, sourceExpr, dataParameter);
                }
            }
        }

        private void AttachDirectlyOutput(KeyValuePair<string, string> output, WorkflowStep step, Type dataType, LambdaExpression sourceExpr, ParameterExpression dataParameter)
        {
            Expression targetProperty;

            // Check if our datatype has a matching property
            var propertyInfo = dataType.GetProperty(output.Key);
            if (propertyInfo != null)
            {
                targetProperty = Expression.Property(dataParameter, propertyInfo);
                var targetExpr = Expression.Lambda(targetProperty, dataParameter);
                step.Outputs.Add(new MemberMapParameter(sourceExpr, targetExpr));
            }
            else
            {
                // If we did not find a matching property try to find a Indexer with string parameter
                propertyInfo = dataType.GetProperty("Item");
                targetProperty = Expression.Property(dataParameter, propertyInfo, Expression.Constant(output.Key));

                Action<IStepBody, object> acn = (pStep, pData) =>
                {
                    object resolvedValue = sourceExpr.Compile().DynamicInvoke(pStep); ;
                    propertyInfo.SetValue(pData, resolvedValue, new object[] { output.Key });
                };

                step.Outputs.Add(new ActionParameter<IStepBody, object>(acn));
            }

        }

        private void AttachNestedOutput(KeyValuePair<string, string> output, WorkflowStep step, StepSourceV1 source, LambdaExpression sourceExpr, ParameterExpression dataParameter)
        {
            PropertyInfo propertyInfo = null;
            String[] paths = output.Key.Split('.');

            Expression targetProperty = dataParameter;

            bool hasAddOutput = false;

            foreach (String propertyName in paths)
            {
                if (hasAddOutput)
                {
                    throw new ArgumentException($"Unknown property for output {output.Key} on {source.Id}");
                }

                if (targetProperty == null)
                {
                    break;
                }

                if (propertyName.Contains("["))
                {
                    String[] items = propertyName.Split('[');

                    if (items.Length != 2)
                    {
                        throw new ArgumentException($"Unknown property for output {output.Key} on {source.Id}");
                    }

                    items[1] = items[1].Trim().TrimEnd(']').Trim().Trim('"');

                    MemberExpression memberExpression = Expression.Property(targetProperty, items[0]);

                    if (memberExpression == null)
                    {
                        throw new ArgumentException($"Unknown property for output {output.Key} on {source.Id}");
                    }
                    propertyInfo = ((PropertyInfo)memberExpression.Member).PropertyType.GetProperty("Item");

                    Action<IStepBody, object> acn = (pStep, pData) =>
                    {
                        var targetExpr = Expression.Lambda(memberExpression, dataParameter);
                        object data = targetExpr.Compile().DynamicInvoke(pData);
                        object resolvedValue = sourceExpr.Compile().DynamicInvoke(pStep); ;
                        propertyInfo.SetValue(data, resolvedValue, new object[] { items[1] });
                    };

                    step.Outputs.Add(new ActionParameter<IStepBody, object>(acn));
                    hasAddOutput = true;
                }
                else
                {
                    try
                    {
                        targetProperty = Expression.Property(targetProperty, propertyName);
                    }
                    catch
                    {
                        targetProperty = null;
                        break;
                    }
                }
            }

            if (targetProperty != null && !hasAddOutput)
            {
                var targetExpr = Expression.Lambda(targetProperty, dataParameter);
                step.Outputs.Add(new MemberMapParameter(sourceExpr, targetExpr));
            }
        }

        private void AttachOutcomes(StepSourceV1 source, Type dataType, WorkflowStep step)
        {
            if (!string.IsNullOrEmpty(source.NextStepId))
                step.Outcomes.Add(new ValueOutcome { ExternalNextStepId = $"{source.NextStepId}" });

            var dataParameter = Expression.Parameter(dataType, "data");
            var outcomeParameter = Expression.Parameter(typeof(object), "outcome");

            foreach (var nextStep in source.SelectNextStep)
            {
                var sourceDelegate = DynamicExpressionParser.ParseLambda(new[] { dataParameter, outcomeParameter }, typeof(object), nextStep.Value).Compile();
                Expression<Func<object, object, bool>> sourceExpr = (data, outcome) => System.Convert.ToBoolean(sourceDelegate.DynamicInvoke(data, outcome));
                step.Outcomes.Add(new ExpressionOutcome<object>(sourceExpr)
                {
                    ExternalNextStepId = $"{nextStep.Key}"
                });
            }
        }

        private Type FindType(string name)
        {
            return Type.GetType(name, true, true);
        }

        private static Action<IStepBody, object, IStepExecutionContext> BuildScalarInputAction(KeyValuePair<string, object> input, ParameterExpression dataParameter, ParameterExpression contextParameter, ParameterExpression environmentVarsParameter, PropertyInfo stepProperty)
        {
            var expr = System.Convert.ToString(input.Value);
            var sourceExpr = DynamicExpressionParser.ParseLambda(new[] { dataParameter, contextParameter, environmentVarsParameter }, typeof(object), expr);

            void acn(IStepBody pStep, object pData, IStepExecutionContext pContext)
            {
                object resolvedValue = sourceExpr.Compile().DynamicInvoke(pData, pContext, Environment.GetEnvironmentVariables());
                if (stepProperty.PropertyType.IsEnum)
                    stepProperty.SetValue(pStep, Enum.Parse(stepProperty.PropertyType, (string)resolvedValue, true));
                else
                {
                    if ((resolvedValue != null) && (stepProperty.PropertyType.IsAssignableFrom(resolvedValue.GetType())))
                        stepProperty.SetValue(pStep, resolvedValue);
                    else
                        stepProperty.SetValue(pStep, System.Convert.ChangeType(resolvedValue, stepProperty.PropertyType));
                }
            }
            return acn;
        }

        private static Action<IStepBody, object, IStepExecutionContext> BuildObjectInputAction(KeyValuePair<string, object> input, ParameterExpression dataParameter, ParameterExpression contextParameter, ParameterExpression environmentVarsParameter, PropertyInfo stepProperty)
        {
            void acn(IStepBody pStep, object pData, IStepExecutionContext pContext)
            {
                var stack = new Stack<JObject>();
                var destObj = JObject.FromObject(input.Value);
                stack.Push(destObj);

                while (stack.Count > 0)
                {
                    var subobj = stack.Pop();
                    foreach (var prop in subobj.Properties().ToList())
                    {
                        if (prop.Name.StartsWith("@"))
                        {
                            var sourceExpr = DynamicExpressionParser.ParseLambda(new[] { dataParameter, contextParameter, environmentVarsParameter }, typeof(object), prop.Value.ToString());
                            object resolvedValue = sourceExpr.Compile().DynamicInvoke(pData, pContext, Environment.GetEnvironmentVariables());
                            subobj.Remove(prop.Name);
                            subobj.Add(prop.Name.TrimStart('@'), JToken.FromObject(resolvedValue));
                        }
                    }

                    foreach (var child in subobj.Children<JObject>())
                        stack.Push(child);
                }

                stepProperty.SetValue(pStep, destObj);
            }
            return acn;
        }
    }
}
