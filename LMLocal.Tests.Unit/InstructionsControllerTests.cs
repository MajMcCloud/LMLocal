using System.Threading;
using System.Threading.Tasks;
using LMLocal.Application.Abstractions.Ports;
using LMLocal.Core.Models.Instructions;
using LMLocal.Infrastructure.WebView.Controllers;
using Moq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LMLocal.Tests.Unit
{
    [TestFixture]
    public class InstructionsControllerTests
    {
        [Test]
        public async Task GetInstructionsAsync_ReturnsJson()
        {
            var mock = new Mock<IInstructionsManager>();
            mock.Setup(m => m.GetAsync(It.IsAny<CancellationToken>())).Returns(Task.FromResult("{\"key\":\"value\"}"));
            var controller = new InstructionsController(mock.Object);

            var result = await controller.GetInstructionsAsync();

            Assert.That(result, Is.EqualTo("{\"key\":\"value\"}"));
        }

        [Test]
        public async Task GetInstructionsAsync_WhenThrows_ReturnsEmptyJson()
        {
            var mock = new Mock<IInstructionsManager>();
            mock.Setup(m => m.GetAsync(It.IsAny<CancellationToken>())).Throws(new System.Exception("fail"));
            var controller = new InstructionsController(mock.Object);

            var result = await controller.GetInstructionsAsync();

            Assert.That(result, Is.EqualTo("{}"));
        }

        [Test]
        public async Task UpdateInstructionsAsync_ValidJson_ReturnsSuccessResult()
        {
            var mock = new Mock<IInstructionsManager>();
            mock.Setup(m => m.UpdateAsync("{\"key\":\"value\"}", It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(InstructionUpdateResult.Ok()));
            var controller = new InstructionsController(mock.Object);

            var result = await controller.UpdateInstructionsAsync("{\"key\":\"value\"}");

            Assert.That(JObject.Parse(result).Value<bool>("success"), Is.True);
            mock.Verify(m => m.UpdateAsync("{\"key\":\"value\"}", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task UpdateInstructionsAsync_ValidationFailure_ReturnsErrorResult()
        {
            var mock = new Mock<IInstructionsManager>();
            mock.Setup(m => m.UpdateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(InstructionUpdateResult.Fail("boom")));
            var controller = new InstructionsController(mock.Object);

            var payload = JObject.Parse(await controller.UpdateInstructionsAsync("{\"x\":1}"));

            Assert.That(payload.Value<bool>("success"), Is.False);
            Assert.That(payload.Value<string>("error"), Is.EqualTo("boom"));
        }

        [Test]
        public async Task UpdateInstructionsAsync_NullOrEmpty_ReturnsFailureWithoutCallingManager()
        {
            var mock = new Mock<IInstructionsManager>();
            var controller = new InstructionsController(mock.Object);

            var resultNull = await controller.UpdateInstructionsAsync(null);
            var resultEmpty = await controller.UpdateInstructionsAsync("");

            Assert.That(JObject.Parse(resultNull).Value<bool>("success"), Is.False);
            Assert.That(JObject.Parse(resultEmpty).Value<bool>("success"), Is.False);
            mock.Verify(m => m.UpdateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task UpdateInstructionsAsync_WhenThrows_ReturnsFailureResult()
        {
            var mock = new Mock<IInstructionsManager>();
            mock.Setup(m => m.UpdateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Throws(new System.Exception("fail"));
            var controller = new InstructionsController(mock.Object);

            var result = await controller.UpdateInstructionsAsync("{\"x\":1}");

            Assert.That(JObject.Parse(result).Value<bool>("success"), Is.False);
        }

        [Test]
        public async Task UpdateInstructionsSelectedTabAsync_ValidTab_ReturnsTrue()
        {
            var mock = new Mock<IInstructionsManager>();
            mock.Setup(m => m.UpdateSelectedTabAsync("tab1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var controller = new InstructionsController(mock.Object);

            var result = await controller.UpdateInstructionsSelectedTabAsync("tab1");

            Assert.That(result, Is.True);
            mock.Verify(m => m.UpdateSelectedTabAsync("tab1", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task UpdateInstructionsSelectedTabAsync_NullOrEmpty_ReturnsFalse()
        {
            var mock = new Mock<IInstructionsManager>();
            var controller = new InstructionsController(mock.Object);

            var resultNull = await controller.UpdateInstructionsSelectedTabAsync(null);
            var resultEmpty = await controller.UpdateInstructionsSelectedTabAsync("");

            Assert.That(resultNull, Is.False);
            Assert.That(resultEmpty, Is.False);
            mock.Verify(m => m.UpdateSelectedTabAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
