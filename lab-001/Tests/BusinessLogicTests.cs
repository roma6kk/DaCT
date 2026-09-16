using OnlineStore.Server;
using OnlineStore.Shared;

namespace OnlineStore.Tests;

public sealed class BusinessLogicTests
{
    [Fact]
    public void GetProducts_ReturnsProducts()
    {
        var result = new BusinessLogic().Handle(new Request("GetProducts"));

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public void CreateOrder_ReturnsCreatedOrder()
    {
        var result = new BusinessLogic().Handle(new Request("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "2"
        }));

        Assert.True(result.Success);
        Assert.Equal("OrderCreated", result.Event);
        Assert.Equal(170000m, Assert.IsType<Order>(result.Data).Total);
    }

    [Fact]
    public void CancelOrder_RestoresOrder()
    {
        var logic = new BusinessLogic();
        var created = logic.Handle(new Request("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "1"
        }));
        var order = Assert.IsType<Order>(created.Data);

        var result = logic.Handle(new Request("CancelOrder", new Dictionary<string, string>
        {
            ["orderId"] = order.Id.ToString()
        }));

        Assert.True(result.Success);
        Assert.Equal("OrderCancelled", result.Event);
    }

    [Fact]
    public void PayOrder_CreatesPayment()
    {
        var logic = new BusinessLogic();
        var created = logic.Handle(new Request("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = "1", ["productId"] = "2", ["quantity"] = "1"
        }));
        var order = Assert.IsType<Order>(created.Data);

        var result = logic.Handle(new Request("PayOrder", new Dictionary<string, string>
        {
            ["orderId"] = order.Id.ToString()
        }));

        Assert.True(result.Success);
        Assert.Equal("PaymentCompleted", result.Event);
        var payment = Assert.IsType<Payment>(result.Data);
        Assert.Equal(order.Id, payment.OrderId);
        Assert.Equal(order.Total, payment.Amount);
        Assert.Equal("Completed", payment.Status);
    }

    [Fact]
    public void PayOrder_Twice_ReturnsError()
    {
        var logic = new BusinessLogic();
        var created = logic.Handle(new Request("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "1"
        }));
        var order = Assert.IsType<Order>(created.Data);
        var data = new Dictionary<string, string> { ["orderId"] = order.Id.ToString() };

        logic.Handle(new Request("PayOrder", data));
        var result = logic.Handle(new Request("PayOrder", data));

        Assert.False(result.Success);
        Assert.Equal("Заказ уже оплачен", result.Message);
    }

    [Fact]
    public void CancelOrder_PaidOrder_ReturnsError()
    {
        var logic = new BusinessLogic();
        var created = logic.Handle(new Request("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "1"
        }));
        var order = Assert.IsType<Order>(created.Data);

        logic.Handle(new Request("PayOrder", new Dictionary<string, string>
        {
            ["orderId"] = order.Id.ToString()
        }));
        var result = logic.Handle(new Request("CancelOrder", new Dictionary<string, string>
        {
            ["orderId"] = order.Id.ToString()
        }));

        Assert.False(result.Success);
        Assert.Equal("Оплаченный заказ нельзя отменить", result.Message);
    }

    [Fact]
    public void UnknownOperation_ReturnsError()
    {
        var result = new BusinessLogic().Handle(new Request("Unknown"));

        Assert.False(result.Success);
    }

    [Fact]
    public void InvalidData_ReturnsError()
    {
        var result = new BusinessLogic().Handle(new Request("CreateOrder"));

        Assert.False(result.Success);
    }

    [Fact]
    public void MissingOrder_ReturnsError()
    {
        var result = new BusinessLogic().Handle(new Request("PayOrder", new Dictionary<string, string>
        {
            ["orderId"] = "999"
        }));

        Assert.False(result.Success);
    }
}
