using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MudBlazor;
using RoslynRunner;
using RoslynRunner.UI;
using RoslynRunner.UI.Pages;

namespace RosylnRunner.EndToEndTests;

[NonParallelizable]
[TestFixture]
public class Tests : PageTest
{
    const int WaitTime = 30000;
    [Test]
    public async Task UiCanRunSampleConversion()
    {
        await Page.GotoAsync(RunnerContext.BaseUrl);
        var selector = Page.Locator("[name=commandType]");
        Assert.That(selector, Is.Not.Null);
        var optionsSelected = await selector.SelectOptionAsync(["Custom Processor"]);
        Assert.That(optionsSelected.Single(), Is.EqualTo("Custom Processor"));

        await Page.WaitForTimeoutAsync(WaitTime);
        var baseDirectory = RunnerContext.BaseDirectory!;
        var sampleRoot = Path.Combine(baseDirectory, "samples", "LegacyWebApp");
        string targetFile = Path.Combine(sampleRoot, "ModernWebApi","Endpoints","SampleEndpoint.cs");
        if (File.Exists(targetFile))
        {
            File.Delete(targetFile);
        }
        Assert.That(File.Exists(targetFile), Is.False);
        var legacyWebappSln = Path.Combine(sampleRoot, "LegacyWebApp.sln");
        Assert.That(File.Exists(legacyWebappSln), Is.True);

        var legacyWebAppConverterCsproj = Path.Combine(sampleRoot, "LegacyWebAppConverter", "LegacyWebAppConverter.csproj");
        
        await Page.GetByLabel(MainPage.Labels.PrimarySolution).FillAsync(legacyWebappSln);

        await Page.WaitForSelectorAsync("[id=customProcessorFields]");
        var processorSolution = Page.GetByLabel(MainPage.Labels.ProcessorSolution);
        await processorSolution.WaitForAsync();
        await processorSolution.FillAsync(legacyWebAppConverterCsproj);
        
        await Page.GetByLabel(MainPage.Labels.ProcessorName).FillAsync("LegacyWebAppConverter.ConvertToMinimalApi");
        await Page.GetByLabel(MainPage.Labels.ProcessorProjectName).FillAsync("LegacyWebAppConverter");

        await Page.GetByText("SUBMIT").ClickAsync();
        await Page.WaitForTimeoutAsync(WaitTime);
        
        var runResult = await RunnerContext.ApiRequestContext.GetAsync("/runs");
        var runs = await runResult.FromJson<List<RunParameters>>();
        Assert.That(runs?.Count, Is.AtLeast(1));
        
        var run = runs.Last(r => r.RunCommand.ProcessorSolution == legacyWebAppConverterCsproj );
        Assert.That(run.RunId, Is.Not.EqualTo(Guid.Empty));
        var runResponse = await RunnerContext.ApiRequestContext.GetAsync($"/runs/{run.RunId}" );
        Assert.That(runResponse.Status, Is.EqualTo(200));
        var json = await runResponse.JsonAsync();
        Assert.That(File.Exists(targetFile), Is.True);
    }
}
