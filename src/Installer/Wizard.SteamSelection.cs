using System.Text.Json;

namespace DeadlockTranslate.Setup;

public sealed partial class Wizard
{
    private bool accountChoicesLoaded;
    private readonly string[] requestedSteamIds;
    private bool steamOnly;
    private SteamManagementReturn? steamManagementReturn;
    private bool steamManagementSubmitted;

    private sealed record SteamManagementReturn(int Step,string Game,string Runtime,string Steam,
        Account[] Accounts,string[] Selected,bool AccountsLoaded,bool StopSteam,bool Admin,string Error);

    private void OpenSteamManagement()
    {
        if(!ready || busy || writing || steamOnly)return;
        steamManagementReturn=new(step,game.Text,runtime.Text,steam.Text,user.Items.Cast<Account>().ToArray(),
            SelectedSteamIds(),accountChoicesLoaded,stopSteam.Checked,admin.Checked,errorMessage);
        steamManagementSubmitted=false;
        steamOnly=true;
        step=1;
        errorMessage="";
        runtime.Text=Str(snapshot,"runtime",runtime.Text);
        Draw();
    }

    private void CloseSteamManagement()
    {
        if(busy || !steamOnly)return;
        steamOnly=false;
        var previousState=steamManagementReturn;
        steamManagementReturn=null;
        step=previousState?.Step??0;
        errorMessage=previousState?.Error??"";
        if(previousState!=null){
            game.Text=previousState.Game;runtime.Text=previousState.Runtime;steam.Text=previousState.Steam;
            user.BeginUpdate();
            try{user.Items.Clear();user.Items.AddRange(previousState.Accounts.Cast<object>().ToArray());SetSteamSelection(previousState.Selected);}
            finally{user.EndUpdate();}
            accountChoicesLoaded=previousState.AccountsLoaded;
            stopSteam.Checked=previousState.StopSteam;admin.Checked=previousState.Admin;
        }
        if(steamManagementSubmitted && step==2){
            // An open install preview may have bound the previous Steam file hashes.
            plan=default;reviewed=null;step=1;
        }
        steamManagementSubmitted=false;
        Draw();
    }

    private string[] SelectedSteamIds() => user.CheckedItems.Cast<Account>().Select(account => account.Id).ToArray();
    private bool ConfigureSelectedSteam => configure.Checked && (Mode == "uninstall" || user.CheckedItems.Count>0);

    private void SetSteamSelection(IEnumerable<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < user.Items.Count; i++)
            user.SetItemChecked(i, wanted.Contains(((Account)user.Items[i]).Id));
    }

    private void ReadSteamAccounts(JsonElement source)
    {
        var selected = SelectedSteamIds();
        var accounts = Items(Field(source,"users"))
            .Select(x => new Account(Str(x,"id"),Flag(x,"active"),Str(x,"name"))).ToArray();
        user.BeginUpdate();
        try {
            user.Items.Clear();
            user.Items.AddRange(accounts.Cast<object>().ToArray());
            if (accountChoicesLoaded) SetSteamSelection(selected);
            else if (requestedSteamIds.Length > 0) SetSteamSelection(requestedSteamIds);
            else SetSteamSelection(accounts.Where(a=>a.Active || accounts.Length == 1).Select(a=>a.Id));
            accountChoicesLoaded = true;
        } finally { user.EndUpdate(); }
    }

    private void DrawSteamAccounts(FlowLayoutPanel card)
    {
        Label(card,"配置哪些 Steam 账号");
        user.Tag = "full-width";user.BackColor=InstallerColors.Paper;user.ForeColor=InstallerColors.Ink;
        user.Height = Math.Clamp(user.Items.Count * user.ItemHeight + 10, 62, 156);
        card.Controls.Add(user);
        if (user.Items.Count == 0) Note(card,"未读到账号。请确认 Steam 目录后点击“读取账号”。");
        else ButtonRow(card, Button("仅选当前登录",()=>SetSteamSelection(user.Items.Cast<Account>().Where(a=>a.Active).Select(a=>a.Id))),
            Button("清空选择",()=>SetSteamSelection([])));
        Note(card,"可勾选多个账号。只配置所选账号，保留其原有游戏参数。留空则手动配置。");
    }

    private void DrawSteamOnly()
    {
        var card=Section("Steam 启动项", "退出 Deadlock 后可执行写入。预览可以在游戏运行时查看。");
        Row(card,"Steam 目录",steam,Button("浏览…",()=>Browse(steam)),AsyncButton("读取账号",async()=>await Work(Inspect)));
        DrawSteamAccounts(card);
        Check(card,stopSteam);
        if(errorMessage.Length==0)Note(card,"只调整所选账号的 Deadlock 启动项。原文件会先备份。");
        ButtonRow(card,Button("返回安装器",CloseSteamManagement));
    }

    private void DrawSteamPreview(FlowLayoutPanel section)
    {
        var accounts=Items(Field(plan,"steamAccounts")).ToArray();
        if(accounts.Length==0)return;
        Label(section,"Steam 账号检查");
        foreach(var account in accounts)
        {
            Note(section,DescribeSteamAccount(account));
        }
    }

    private string DescribeSteamAccount(JsonElement account)
    {
        var id=Str(account,"id");
        var name=user.Items.Cast<Account>().FirstOrDefault(a=>a.Id==id)?.ToString()??id;
        var status=Flag(account,"skipped")?"跳过 · "+Str(account,"reason"):
            Str(account,"error") is {Length:>0} problem?problem:Flag(account,"alreadyConfigured")?"已匹配":"将配置启动项";
        return name+" · "+status;
    }

    private void LogSkippedSteamAccounts(JsonElement accounts)
    {
        foreach(var account in Items(accounts).Where(value=>Flag(value,"skipped")))Log(DescribeSteamAccount(account));
    }
}
