namespace StateMachineCodeGenerator
{
    public abstract class StateComponentTypeTemplate
    {
        public const string Template = @"
namespace StateMachine.Base
{
    public static partial class StateComponentTypeUtils
    {
        static StateComponentTypeUtils()
        {

            #region MapCode

            #endregion

        }
    }
}
";
    }
}