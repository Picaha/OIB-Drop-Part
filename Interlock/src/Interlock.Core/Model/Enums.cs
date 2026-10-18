namespace Interlock.Core.Model
{
    public enum LockType { Feeder, Pu, Pn, Mpn, Supplier, Conveyor }

    public enum LockStatus { Active, Released }

    public enum LockSource { Manual, Api, SetupCenter }

    public enum EnforceState { Pending, Applied, Failed, NotNeeded }

    public enum Lanes { Left, Right, Both }

    public enum Role { Operator, Qa, Engineer, Admin }
}
