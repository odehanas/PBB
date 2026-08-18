using System.Collections.Generic;

namespace GovBudget.Models
{
    public class StructureEntityNode
    {
        public int EntityId { get; set; }
        public string EntityCode { get; set; } = "";
        public string EntityName { get; set; } = "";
        public bool IsActive { get; set; }
        public List<StructureDepartmentNode> Departments { get; set; } = new();
    }

    public class StructureDepartmentNode
    {
        public int DepartmentId { get; set; }
        public string DeptCode { get; set; } = "";
        public string DeptName { get; set; } = "";
        public bool IsActive { get; set; }
        public List<StructureProgramNode> Programs { get; set; } = new();
    }

    public class StructureProgramNode
    {
        public int ProgramId { get; set; }
        public string ProgramCode { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public string ProgramType { get; set; } = "Mandate";
        public bool IsActive { get; set; }
        public List<StructureActivityNode> Activities { get; set; } = new();
    }

    public class StructureActivityNode
    {
        public int ActivityId { get; set; }
        public string ActivityCode { get; set; } = "";
        public string ActivityName { get; set; } = "";
        public bool IsActive { get; set; }
        public List<StructureProjectNode> Projects { get; set; } = new();
    }

    public class StructureProjectNode
    {
        public int ProjectId { get; set; }
        public string ProjectCode { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public bool IsActive { get; set; }
    }
}
