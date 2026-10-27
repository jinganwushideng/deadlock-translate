namespace DeadlockTranslate;
public enum TranslationOutputFailure { ReasoningBudget,Truncated,NamesMissing,ReasoningOnly }
// Fixed messages only: never forward a model response or reasoning into UI/logs.
public sealed class TranslationOutputException(TranslationOutputFailure failure):Exception(failure switch{
    TranslationOutputFailure.ReasoningBudget=>"模型思考耗尽输出额度，没有生成译文。请在“翻译服务”适当提高输出上限或主动关闭思考，并检查精翻/发送超时；原文已保留，未自动重试。",
    TranslationOutputFailure.Truncated=>"模型译文输出被截断，未采用不完整内容。请在“翻译服务”适当提高模型输出上限，重试前确认原文。",
    TranslationOutputFailure.NamesMissing=>"译文专名校验仍未通过，未发送；已保留原文。请核对词库中的规范名称，或调整模型后重试。发送翻译会先尝试本地名称纠正，必要时最多重新翻译一次。",
    _=>"模型只返回思考内容，没有可用译文。请检查输出上限和模型返回格式。"
}){
    public LogReason Reason=>failure switch{TranslationOutputFailure.ReasoningBudget=>LogReason.ReasoningBudget,TranslationOutputFailure.Truncated=>LogReason.OutputTruncated,TranslationOutputFailure.NamesMissing=>LogReason.NameMismatch,_=>LogReason.InvalidData};
}
