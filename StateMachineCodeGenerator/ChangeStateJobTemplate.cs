namespace StateMachineCodeGenerator
{
    public abstract class ChangeStateJobTemplate
    {
        public const string Template = @"
using Unity.Collections;
using Unity.Entities;

// ReSharper disable RedundantNameQualifier

namespace StateMachine.Base
{
    public partial struct ChangeStateJob
    {
        int GetComponentTypeCount()
        {

            #region TypeCount

            #endregion

        }

        #region LookUpCode

        #endregion

        void AddComponent(Entity entity, int componentType, int sortKey, Entity nextState)
        {
            switch (componentType)
            {

                #region AddComponentCode

                #endregion

            }
        }

        void RemoveComponent(Entity entity, int componentType)
        {
            switch (componentType)
            {

                #region RemoveComponentCode

                #endregion

            }
        }

        void ChangeComponent(Entity entity, int componentType, Entity nextState)
        {
            switch (componentType)
            {

                #region ChangeComponentCode

                #endregion

            }
        }

        public void CreateLookups(SystemBase system)
        {

            #region LookUpCreateCode

            #endregion

        }
    }
}
";
    }
}