namespace DeadlockTranslate;

public enum HotkeyAction { Pass, Trigger, Test, Suppress }
public readonly record struct HotkeyContext(bool Enabled,bool ChatOpen,bool Fresh,bool GameForeground,bool TestAllowed,bool TestActive);
public readonly record struct HotkeyDecision(HotkeyAction Action,LogReason Reason=LogReason.None);
public sealed class HotkeyState {
    public KeyModifiers Held {get;private set;}
    public int SwallowedKey {get;private set;}
    public void Seed(KeyModifiers modifiers){Held=modifiers;}
    public void RecoverRelease(bool physicallyDown){if(!physicallyDown)SwallowedKey=0;}
    public HotkeyDecision Process(HotkeyChord chord,int key,int scan,int flags,bool down,bool up,HotkeyContext context){
        var modifier=HotkeyChord.Modifier(key,scan,flags);
        if(modifier!=0){if(down)Held|=modifier;else if(up)Held&=~modifier;return new(HotkeyAction.Pass);}
        if(key==SwallowedKey){if(up)SwallowedKey=0;return new(HotkeyAction.Suppress);}
        if(!down||key!=chord.VirtualKey)return new(HotkeyAction.Pass);
        if(!chord.Matches(Held))return new(HotkeyAction.Pass,LogReason.ModifiersMismatch);
        if((flags&0x10)!=0)return new(HotkeyAction.Pass,LogReason.Injected);
        if(context.TestActive&&context.TestAllowed){SwallowedKey=key;return new(HotkeyAction.Test);}
        if(!context.GameForeground)return new(HotkeyAction.Pass,LogReason.ForegroundOther);
        if(!context.Enabled)return new(HotkeyAction.Pass,LogReason.Disabled);
        if(!context.Fresh)return new(HotkeyAction.Pass,LogReason.StaleBridge);
        if(!context.ChatOpen)return new(HotkeyAction.Pass,LogReason.NoChat);
        SwallowedKey=key;return new(HotkeyAction.Trigger);
    }
}
