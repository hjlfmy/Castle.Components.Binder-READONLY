// ${Copyrigth}

namespace Castle.CastleOnRails.Framework.Tests
{
	using System;
	using System.Reflection;

	using Castle.CastleOnRails.Framework.Internal;
	using Castle.CastleOnRails.Framework.Tests.Controllers;

	using NUnit.Framework;
	
	[TestFixture]
	public class HelperTestCase
	{
		private DefaultControllerFactory _factory;

		public HelperTestCase()
		{
		}

		[SetUp]
		public void Init()
		{
			_factory = new DefaultControllerFactory();
			_factory.Inspect( Assembly.GetExecutingAssembly() );
		}

		[Test]
		public void GetHelpersFromAttributes()
		{
			HelperController controller = _factory.GetController(new UrlInfo("", "", "helper", "", "rails")) as HelperController;

			object helper = controller.Helpers[typeof(BarHelper).Name];

			Assert.IsNotNull(helper);
			Assert.IsTrue(helper is BarHelper);
		}

	}
}
