using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace all1box.io.Controllers;

[Authorize]
public sealed class AdminController : Controller
{
    [HttpGet("/Admin")]
    public IActionResult Index()
    {
        return View();
    }
}
