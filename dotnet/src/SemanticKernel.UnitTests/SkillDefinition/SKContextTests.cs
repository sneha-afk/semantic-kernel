﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.SkillDefinition;
using Moq;
using Xunit;

namespace SemanticKernel.UnitTests.SkillDefinition;

public class SKContextTests
{
    private readonly Mock<IReadOnlySkillCollection> _skills;
    private readonly Mock<ILoggerFactory> _logger;

    public SKContextTests()
    {
        this._skills = new Mock<IReadOnlySkillCollection>();
        this._logger = new Mock<ILoggerFactory>();
    }

    [Fact]
    public void ItHasHelpersForContextVariables()
    {
        // Arrange
        var variables = new ContextVariables();
        var target = new SKContext(variables, skills: this._skills.Object, loggerFactory: this._logger.Object);
        variables.Set("foo1", "bar1");

        // Act
        target.Variables["foo2"] = "bar2";
        target.Variables["INPUT"] = Guid.NewGuid().ToString("N");

        // Assert
        Assert.Equal("bar1", target.Variables["foo1"]);
        Assert.Equal("bar1", target.Variables["foo1"]);
        Assert.Equal("bar2", target.Variables["foo2"]);
        Assert.Equal("bar2", target.Variables["foo2"]);
        Assert.Equal(target.Variables["INPUT"], target.Result);
        Assert.Equal(target.Variables["INPUT"], target.ToString());
        Assert.Equal(target.Variables["INPUT"], target.Variables.Input);
        Assert.Equal(target.Variables["INPUT"], target.Variables.ToString());
    }

    [Fact]
    public async Task ItHasHelpersForSkillCollectionAsync()
    {
        // Arrange
        IDictionary<string, ISKFunction> skill = KernelBuilder.Create().ImportSkill(new Parrot(), "test");
        this._skills.Setup(x => x.GetFunction("func")).Returns(skill["say"]);
        var target = new SKContext(new ContextVariables(), this._skills.Object, this._logger.Object);
        Assert.NotNull(target.Skills);

        // Act
        var say = target.Skills.GetFunction("func");
        SKContext result = await say.InvokeAsync("ciao");

        // Assert
        Assert.Equal("ciao", result.Result);
    }

    private sealed class Parrot
    {
        [SKFunction, Description("say something")]
        // ReSharper disable once UnusedMember.Local
        public string Say(string input)
        {
            return input;
        }
    }
}
