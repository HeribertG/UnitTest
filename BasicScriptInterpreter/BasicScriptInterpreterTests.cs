using Klacks_api.BasicScriptInterpreter;

namespace UnitTest.BasicScriptInterpreter
{
  internal class BasicScriptInterpreterTests
  {
    [TestCase("debugprint 10/9", "1.1111111111111112")] // Calc
    [TestCase("message 1, 10/9", "1.1111111111111112")]
    [TestCase("debugprint 10\\9", "1")]
    [TestCase("message 1, 10\\9", "1")]
    [TestCase("debugprint 10 mod 3", "1")]
    [TestCase("message 1, 10 mod 3", "1")]
    [TestCase("debugprint 10 ^ 3", "1000")]
    [TestCase("message 1, 10 ^ 3", "1000")]
    [TestCase("debugprint -1 * -1 ", "1")]
    [TestCase("message 1, -1 * -1 ", "1")]
    [TestCase("debugprint 1 * -1 ", "-1")]
    [TestCase("message 1, 1 * -1 ", "-1")]
    [TestCase("debugprint 0 * 1 ", "0")]
    [TestCase("message 1, 0 * 1 ", "0")]
    [TestCase("debugprint 0 / 1 ", "0")]
    [TestCase("message 1, 0 / 1 ", "0")]
    [TestCase("debugprint 1 / 0 ", "∞")]
    [TestCase("message 1, 1 / 0 ", "∞")]
    [TestCase("debugprint 2  + 3 * 4 ", "14")]
    [TestCase("message 1, 2  + 3 * 4 ", "14")]
    [TestCase("debugprint (2  + 3) * 4 ", "20")]
    [TestCase("message 1, (2  + 3) * 4 ", "20")]
    [TestCase("dim x\n\nx = 10\nx += 5\nx *= 3\nx -=10\nx /= 2.5\n\nx &= \" cm\"\n\n debugprint x ", "14 cm")] // Dim and Const Declaration
    [TestCase("dim x\n\nx = 10\nx += 5\nx *= 3\nx -=10\nx /= 2.5\n\nx &= \" cm\"\n\n message 1, x", "14 cm")]
    [TestCase("dim x\n\nx = 10\nx += 5\nx *= 3\nx -=10\nx /= 2.5\n\nx &= \" cm\"\n\n debugprint x ", "14 cm")]
    [TestCase("dim x\n\nx = 10\nx += 5\nx *= 3\nx -=10\nx /= 2.5\n\nx &= \" cm\"\n\n message 1, x", "14 cm")]
    public void Interpreter_Ok(string script, string result)
    {
      //Arrange
      var basicInterpreter = new Code();

      basicInterpreter.Message += (type, msg) =>
      {
        //Assert
        msg.Should().NotBeEmpty();
        type.Should().Be(1);
        msg.Should().Be(result);
      };
      basicInterpreter.DebugPrint += (msg) =>
      {
        //Assert
        msg.Should().NotBeEmpty();
        msg.Should().Be(result);
      };
      //Act
      var isCompiled = basicInterpreter.Compile(script, true, false);

      //Assert
      isCompiled.Should().BeTrue();

      //2. Act
      basicInterpreter.Run();
    }
  }
}
