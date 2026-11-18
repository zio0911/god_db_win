namespace GodDB.Core.Modeling;

/// 개발용 예시 모델 (Mac: Sources/GodDB/ModelingSample.swift 의 model() — 화면 PNG 렌더링 부분은 화면 쪽)
public static class ModelingSample
{
    public static DataModel Model()
    {
        var m = new DataModel(name: "쇼핑몰 모델", kind: DatabaseKind.Mysql);
        var member = m.AddTable(logicalName: "회원");
        var order = m.AddTable(logicalName: "주문");
        var item = m.AddTable(logicalName: "주문상품");
        var product = m.AddTable(logicalName: "상품");
        void Set(Guid id, string physical, string pk, ModelColumn[] cols)
        {
            var t = m.Table(id)!;
            t.PhysicalName = physical;
            t.Columns[0].PhysicalName = pk;
            t.Columns.AddRange(cols);
        }
        Set(member, "tb_member", "id",
        [
            new ModelColumn(logicalName: "이메일", physicalName: "email", notNull: true, unique: true),
            new ModelColumn(logicalName: "이름", physicalName: "name", dataType: "varchar(50)", notNull: true),
            new ModelColumn(logicalName: "가입일시", physicalName: "", dataType: "datetime(3)", notNull: true,
                            defaultValue: "CURRENT_TIMESTAMP(3)"),
        ]);
        Set(order, "tb_order", "id",
        [
            new ModelColumn(logicalName: "주문일시", physicalName: "ordered_at", dataType: "datetime(3)", notNull: true),
            new ModelColumn(logicalName: "결제금액", physicalName: "total", dataType: "decimal(12,2)", notNull: true),
        ]);
        Set(product, "tb_product", "id",
        [
            new ModelColumn(logicalName: "상품명", physicalName: "name", dataType: "varchar(200)", notNull: true),
            new ModelColumn(logicalName: "판매가", physicalName: "price", dataType: "decimal(12,2)"),
        ]);
        Set(item, "tb_order_item", "id", [new ModelColumn(logicalName: "수량", physicalName: "qty", dataType: "int", notNull: true)]);
        m.AddRelation(member, order);
        m.AddRelation(order, item, identifying: true);
        m.AddRelation(product, item);
        (Guid Id, double X, double Y)[] pos = [(member, 40, 60), (order, 380, 60), (item, 720, 60), (product, 380, 330)];
        foreach (var (id, x, y) in pos)
        {
            var t = m.Table(id)!;
            t.X = x;
            t.Y = y;
        }
        m.Table(product)!.Color = "green";
        return m;
    }
}
