using System.Text.Json;
using PlaxisProMax.Chat;
using PlaxisProMax.Mcp;
using PlaxisProMax.Plaxis;
using PlaxisProMax.Settings;
using PlaxisProMax.Tools;

/// <summary>Recipes (save, list, run with placeholders, delete) and redo_steps, against the stand-in Plaxis.</summary>
internal static class RecipeTests
{
    private static ToolArgs Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ToolArgs(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
    }

    public static void Run(Action<string, bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ppm-recipes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Recipes.FolderOverride = Path.Combine(temp, "recipes");
        RedoLog.FolderOverride = Path.Combine(temp, "redo");
        try
        {
            using var link = new PlaxisLink(TestPlaxis.WorkerStart);
            var registry = PlaxisProMax.Tools.ToolRegistry.CreateDefault();
            var settings = new AgentSettings();
            var context = new ToolContext(link, new CodeLibrary(Path.Combine(temp, "kept")), () => settings);
            ToolResult Call(string tool, string json) => registry.Find(tool)!.ExecuteAsync(context, Args(json), CancellationToken.None).GetAwaiter().GetResult();

            // Placeholders take the type the tool's schema asks for.
            var filled = Placeholders.Fill(Args("""{"steps":"{n}","name":"Stage {n}","settings":{"a":"{flag}"}}""").Raw.ToDictionary(p => p.Key, p => p.Value),
                """{"steps":{"type":"integer"},"name":{"type":"string"},"settings":{"type":"object","properties":{"a":{"type":"boolean"}}}}""",
                Args("""{"n":"3","flag":"yes"}""").Raw.ToDictionary(p => p.Key, p => p.Value));
            check("placeholder: a whole value becomes the schema's integer", filled["steps"].ValueKind == JsonValueKind.Number && filled["steps"].GetInt32() == 3, filled["steps"].GetRawText());
            check("placeholder: inside text it is replaced as text", filled["name"].GetString() == "Stage 3", filled["name"].GetRawText());
            check("placeholder: nested values follow the nested schema", filled["settings"].GetProperty("a").ValueKind == JsonValueKind.True, filled["settings"].GetRawText());

            // Save: unknown tools and recipes inside recipes are refused.
            check("save_recipe refuses an unknown tool", Call("save_recipe", """{"name":"x","steps":[{"tool":"no_such_tool"}]}""") is { IsError: true } bad && bad.Text.Contains("no_such_tool"), "");
            check("save_recipe refuses a recipe in a recipe", Call("save_recipe", """{"name":"x","steps":[{"tool":"run_recipe","arguments":{"name":"x"}}]}""").IsError, "");
            check("save_recipe refuses no steps", Call("save_recipe", """{"name":"x","steps":[]}""").IsError, "");
            var saved = Call("save_recipe", """{"name":"Stage: excavate","description":"A phase for one excavation level","steps":[{"tool":"add_phase","arguments":{"name":"Excavate to {level}"}},{"tool":"list_phases"}]}""");
            check("save_recipe saves", !saved.IsError && Directory.GetFiles(Recipes.Folder, "*.json").Length == 1, saved.Text);
            check("recipe file name is safe", !Path.GetFileName(Directory.GetFiles(Recipes.Folder)[0]).Contains(':'), Directory.GetFiles(Recipes.Folder)[0]);

            var listed = Call("list_recipes", "{}");
            check("list_recipes shows the recipe, its steps and its placeholder", listed.Text.Contains("Stage: excavate") && listed.Text.Contains("add_phase") && listed.Text.Contains("\"level\""), listed.Text);

            // The approval card shows every step of the recipe.
            var card = ChatMcp.Details("run_recipe", Args("""{"name":"Stage: excavate","values":{"level":"-3"}}""").Raw);
            check("the approval card lists the recipe's steps", card.Contains("1. add_phase") && card.Contains("2. list_phases") && card.Contains("-3"), card);

            // Run: a missing value stops before anything runs; with the value, both steps run and the change is kept for redo.
            check("run_recipe: an unknown recipe says so", Call("run_recipe", """{"name":"nope"}""") is { IsError: true } none && none.Text.Contains("list_recipes"), "");
            link.ConnectAsync(10000, "secret", CancellationToken.None).GetAwaiter().GetResult();
            var noValue = Call("run_recipe", """{"name":"Stage: excavate"}""");
            check("run_recipe: a placeholder with no value stops the run", noValue.IsError && noValue.Text.Contains("no value was given for: level"), noValue.Text);
            var ran = Call("run_recipe", """{"name":"Stage: excavate","values":{"level":"-3"}}""");
            check("run_recipe runs both steps", !ran.IsError && ran.Text.Contains("\"step\":2") && ran.Text.Contains("Excavate to -3"), ran.Text);
            var project = RedoLog.ProjectOf(context);
            var kept = RedoLog.Read(project);
            check("the recipe's change is kept for redo, the read is not", kept.Count == 1 && kept[0].Tool == "add_phase" && kept[0].From.Contains("Stage: excavate"), string.Join(",", kept.Select(k => k.Tool)));

            // Redo: list reads only, replay runs the kept step again with its own arguments.
            check("redo list is not a change", !ChatMcp.Changes(registry.Find("redo_steps")!, "redo_steps", Args("{}").Raw) && ChatMcp.Changes(registry.Find("redo_steps")!, "redo_steps", Args("""{"action":"replay","steps":[1]}""").Raw), "");
            RedoLog.Write(project, "run_commands", Args("""{"commands":["_gotostages"]}""").Raw, "ok");
            var list = Call("redo_steps", "{}");
            check("redo_steps lists the kept steps with what they did", list.Text.Contains("\"count\":2") && list.Text.Contains("add_phase") && list.Text.Contains("_gotostages"), list.Text);
            check("redo_steps: since a later time lists nothing", Call("redo_steps", $$"""{"since":"{{DateTime.Now.AddDays(1):yyyy-MM-dd HH:mm}}"}""").Text.Contains("\"count\":0"), "");
            check("redo_steps: replay needs steps", Call("redo_steps", """{"action":"replay"}""").IsError, "");
            check("redo_steps: an unknown step says so", Call("redo_steps", """{"action":"replay","steps":[9]}""") is { IsError: true } missing && missing.Text.Contains("No kept step 9"), "");
            var before = Call("list_phases", "{}").Text;
            var again = Call("redo_steps", """{"action":"replay","steps":[1]}""");
            var after = Call("list_phases", "{}").Text;
            check("redo_steps runs the step again", !again.IsError && again.Text.Contains("\"ran\":1") && again.Text.Contains("\"failed\":0") && after.Length > before.Length, again.Text + " | " + after);
            check("a redo is not kept twice", RedoLog.Read(project).Count == 2, RedoLog.Read(project).Count.ToString());
            check("redo_steps: another project by words, and an unknown one", Call("redo_steps", """{"project":"no such project"}""") is { IsError: true } unknown && unknown.Text.Contains("Projects with kept steps"), "");
            check("redo time: HH:mm is today", RedoStepsTool.Time("08:30") == DateTime.Today.AddHours(8.5) && RedoStepsTool.Time("") is null && RedoStepsTool.Time("rubbish") is null, "");

            // work_progress: the plan of a long job, kept per project.
            WorkProgressTool.FolderOverride = Path.Combine(temp, "progress");
            check("work_progress: nothing saved yet", Call("work_progress", """{"action":"get"}""").Text.Contains("No saved plan"), "");
            var plan = Call("work_progress", """{"action":"set","goal":"Logs to report","steps":["Soil model","Phases","Calculate"]}""");
            check("work_progress set names the next step", plan.Text.Contains("1. Soil model"), plan.Text);
            var done = Call("work_progress", """{"action":"update","step":1,"status":"done","note":"BH-1 to BH-4, five layers"}""");
            check("work_progress update moves on and keeps the note", done.Text.Contains("2. Phases") && done.Text.Contains("five layers"), done.Text);
            check("work_progress get reads it back", Call("work_progress", """{"action":"get"}""").Text.Contains("Logs to report"), "");
            check("work_progress clear", Call("work_progress", """{"action":"clear"}""").Text.Contains("cleared") && Call("work_progress", """{"action":"get"}""").Text.Contains("No saved plan"), "");
            WorkProgressTool.FolderOverride = null;

            // kept_code: the whole list, and deleting one entry.
            var keptEntry = context.Library.Keep("g_i.gotostages()\nprint(len(g_i.Phases))", "Count the phases", null, "PLAXIS 2D", "2024.2", allowed: true);
            var keptList = Call("kept_code", "{}");
            check("kept_code lists what is kept", keptList.Text.Contains("Count the phases") && keptList.Text.Contains(keptEntry.Id), keptList.Text);
            check("kept_code deletes an entry", !Call("kept_code", $$"""{"action":"delete","id":"{{keptEntry.Id}}"}""").IsError && context.Library.Get(keptEntry.Id) is null, "");
            check("kept_code: deleting an unknown one says so", Call("kept_code", """{"action":"delete","id":"nope"}""").IsError, "");

            check("delete_recipe deletes", !Call("delete_recipe", """{"name":"Stage: excavate"}""").IsError && Directory.GetFiles(Recipes.Folder, "*.json").Length == 0, "");
            check("delete_recipe: an unknown one says so", Call("delete_recipe", """{"name":"Stage: excavate"}""").IsError, "");
        }
        finally
        {
            Recipes.FolderOverride = null;
            RedoLog.FolderOverride = null;
            try { Directory.Delete(temp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>ask_user: only in an autopilot with the phone on; ask, wait, the answer, and a late answer.</summary>
internal static class AskUserTests
{
    public static void Run(Action<string, bool, string> check)
    {
        using var link = new PlaxisLink(TestPlaxis.WorkerStart);
        var settings = new AgentSettings();
        var context = new ToolContext(link, new CodeLibrary(Path.Combine(Path.GetTempPath(), "ppm-ask-" + Guid.NewGuid().ToString("N"))), () => settings);
        var tool = new AskUserTool();
        ToolResult Call(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return tool.ExecuteAsync(context, new ToolArgs(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())), CancellationToken.None).GetAwaiter().GetResult();
        }
        AskUserTool.Clear();
        try
        {
            check("ask_user outside an autopilot points to the Questions tab", Call("""{"question":"a","assumed":"b"}""") is { IsError: true } no && no.Text.Contains("chat_questions"), "");
            AskUserTool.CanAskOverride = () => true;
            check("ask_user needs the question and the assumption", Call("""{"question":"a"}""").IsError, "");
            check("ask_user with nobody to send says so", Call("""{"question":"a","assumed":"b"}""") is { IsError: true } unsent && unsent.Text.Contains("couldn't be sent"), "");
            var sent = new List<UserQuestion>();
            AskUserTool.Sender = q => { sent.Add(q); return true; };
            var asked = Call("""{"question":"Prestress 150 or 200 kN/m?","assumed":"150 kN/m"}""");
            check("ask_user sends the question with its assumption", !asked.IsError && sent.Count == 1 && sent[0].Assumed == "150 kN/m" && asked.Text.Contains("\"id\":1"), asked.Text);
            check("ask_user wait needs a known id", Call("""{"action":"wait","id":7}""").IsError, "");
            _ = Task.Run(async () => { await Task.Delay(300); AskUserTool.Answer(1, "200"); });
            var got = Call("""{"action":"wait","id":1}""");
            check("ask_user wait returns the answer when it comes", got.Text.Contains("\"answered\":true") && got.Text.Contains("200") && got.Text.Contains("150 kN/m"), got.Text);
            check("a second answer changes nothing", ((Action)(() => AskUserTool.Answer(1, "none")))is not null && Call("""{"action":"wait","id":1}""").Text.Contains("\"answer\":\"200\""), "");

            // No reply in time: the job goes on; the answer that comes later is told to the chat.
            Call("""{"question":"Second?","assumed":"yes"}""");
            sent[1].Sent = DateTime.UtcNow.AddMinutes(-60);
            var none = Call("""{"action":"wait","id":2}""");
            check("ask_user: the wait is over, carry on with the assumption", none.Text.Contains("\"answered\":false") && none.Text.Contains("carry on with your assumption"), none.Text);
            UserQuestion? late = null;
            Action<UserQuestion> hear = q => late = q;
            AskUserTool.Late += hear;
            AskUserTool.Answer(2, "no");
            AskUserTool.Late -= hear;
            check("a late answer is told to the chat", late is { Id: 2, Answer: "no" }, "");
            check("ask_user is not a project change and has a plain name", !tool.ModifiesModel && PlaxisProMax.Chat.StepNames.ReadOnly("ask_user") && PlaxisProMax.Chat.StepNames.Title("ask_user") == "Ask you on your phone", "");
        }
        finally
        {
            AskUserTool.CanAskOverride = null;
            AskUserTool.Sender = null;
            AskUserTool.Clear();
        }
    }
}
