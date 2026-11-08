using System.Text.Json;

namespace DeadlockTranslate.Setup;

public sealed partial class Wizard
{
    private JsonElement runtimeChoice;

    private void ShowRuntimeChoice(JsonElement result)
    {
        runtimeChoice = Field(result, "runtimeChoice");
        game.Text = Str(result, "game", game.Text);
        runtime.Text = Str(runtimeChoice, "current", runtime.Text);
        step = 1;
        Log("选择本次程序目录后进入安装预览。");
    }

    private void DrawRuntimeChoice()
    {
        if (runtimeChoice.ValueKind != JsonValueKind.Object) return;
        var current = Str(runtimeChoice, "current");
        var previous = Str(runtimeChoice, "previous");
        var hash = Str(runtimeChoice, "recordHash");
        var section = Section("本次选择与上次程序目录不同");
        Label(section, "当前目录：" + current + "\n上次目录：" + previous);
        if (Flag(runtimeChoice, "canUseCurrent"))
            ButtonRow(section, AsyncButton("使用当前目录", () => PlanInstallation(current, hash)),
                AsyncButton("沿用上次目录", () => PlanInstallation(previous)));
        else
        {
            Note(section, Str(runtimeChoice, "reason"));
            ButtonRow(section, AsyncButton("沿用上次目录", () => PlanInstallation(previous)));
        }
        Note(section, "选择后进入预览。确认执行时才会写入文件。");
    }

    private Task PlanInstallation(string? chosen = null, string? recordHash = null) => Work(async () =>
    {
        runtimeChoice = default;
        if (chosen != null) runtime.Text = chosen;
        var request = Request("plan");
        if (recordHash != null)
        {
            request["runtimeChoice"] = "current";
            request["runtimeChoicePath"] = chosen;
            request["runtimeChoiceRecordHash"] = recordHash;
        }
        plan = await runner!.Run(request);
        if (Field(plan, "runtimeChoice").ValueKind == JsonValueKind.Object)
        {
            ShowRuntimeChoice(plan);
            return;
        }
        if (Flag(plan, "reinstallSuggested"))
        {
            mode.SelectedIndex = 0;
            step = 0;
            Log("没有可验证的安装记录。已切换重新安装，继续即可检查并恢复缺失文件。");
            return;
        }
        allowReadOnly.Checked = related.Checked = false;
        repairMissing.Checked = Flag(plan, "needsRepair");
        repairGameVpk.Checked = Flag(plan, "needsGameRepair") && !Flag(Field(plan, "gameVpk"), "blocked");
        game.Text = Str(plan, "game");
        runtime.Text = Str(plan, "runtime");
        launch.Text = Str(plan, "launch");
        if (Flag(plan, "fullRepair") && !Flag(Field(snapshot, "certificate"), "ready"))
        {
            step = 0;
            Log("成套修复需要有效的本机证书，请在第一步准备或重新检测。");
            return;
        }
        conflicts.Items.Clear();
        foreach (var item in Items(Field(plan, "conflicts")))
            conflicts.Items.Add(new Conflict(Str(item, "file"), Str(item, "hash"), Str(item, "kind"),
                string.Join(", ", Items(Field(item, "resources")).Select(value => value.GetString()))), false);
        step = 2;
    });

    private void BindReviewedRuntime(Dictionary<string, object?> request)
    {
        var move = Field(plan, "runtimeMove");
        if (Str(move, "kind") != "move") return;
        request["runtimeChoice"] = "current";
        request["runtimeChoicePath"] = Str(move, "runtime");
        request["runtimeChoiceRecordHash"] = Str(plan, "recordHash");
    }
}
