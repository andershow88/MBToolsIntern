using Microsoft.AspNetCore.Mvc;

namespace MBToolsIntern.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
    public IActionResult Error() => View();
}
