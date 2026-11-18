namespace GodDB.Core.Schema;

/// 테이블 하나에 대한 구조 변경. 테이블 수정 화면과 스키마 비교가 만들고, DB 별 Dialect 가 SQL 로 바꾼다.
public abstract record SchemaChange
{
    private SchemaChange() { }

    public sealed record DropIndex(IndexMeta Index) : SchemaChange;
    public sealed record DropColumn(string Name) : SchemaChange;
    /// 이름 변경 (속성도 함께 바뀔 수 있음)
    public sealed record RenameColumn(ColumnMeta Old, ColumnMeta New) : SchemaChange;
    public sealed record ModifyColumn(ColumnMeta Old, ColumnMeta New) : SchemaChange;
    /// After == null 이면 맨 앞
    public sealed record AddColumn(ColumnMeta Column, string? After) : SchemaChange;
    public sealed record AddIndex(IndexMeta Index) : SchemaChange;
    public sealed record TableComment(string Old, string New) : SchemaChange;
}
