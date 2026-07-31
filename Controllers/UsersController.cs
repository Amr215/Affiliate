using Affiliate.Identity;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    public class UsersController : Controller
    {
        private readonly UserManager<IdentityUser> _userManager;

        public UsersController(UserManager<IdentityUser> userManager)
        {
            _userManager = userManager;
        }

        public async Task<IActionResult> Index()
        {
            var users = await _userManager.Users
                .OrderBy(u => u.Email)
                .ToListAsync();
            return View(users);
        }

        [HttpGet]
        public IActionResult Create()
        {
            return View(new CreateUserViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreateUserViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var existing = await _userManager.FindByEmailAsync(model.Email);
            if (existing != null)
            {
                ModelState.AddModelError(nameof(model.Email), "هذا البريد الإلكتروني مستخدم مسبقاً.");
                return View(model);
            }

            var user = new IdentityUser
            {
                UserName = model.Email,
                Email = model.Email,
                EmailConfirmed = true
            };

            var createResult = await _userManager.CreateAsync(user, model.Password);
            if (!createResult.Succeeded)
            {
                foreach (var error in createResult.Errors)
                    ModelState.AddModelError(string.Empty, TranslateIdentityError(error.Description));

                return View(model);
            }

            var roleResult = await _userManager.AddToRoleAsync(user, AppRoles.Admin);
            if (!roleResult.Succeeded)
            {
                foreach (var error in roleResult.Errors)
                    ModelState.AddModelError(string.Empty, TranslateIdentityError(error.Description));

                return View(model);
            }

            TempData["Success"] = "تم إنشاء الحساب بنجاح.";
            return RedirectToAction(nameof(Index));
        }

        private static string TranslateIdentityError(string description)
        {
            if (description.Contains("Passwords must have at least one non alphanumeric", StringComparison.OrdinalIgnoreCase))
                return "كلمة المرور يجب أن تحتوي على رمز خاص واحد على الأقل.";
            if (description.Contains("Passwords must have at least one digit", StringComparison.OrdinalIgnoreCase))
                return "كلمة المرور يجب أن تحتوي على رقم واحد على الأقل.";
            if (description.Contains("Passwords must have at least one uppercase", StringComparison.OrdinalIgnoreCase))
                return "كلمة المرور يجب أن تحتوي على حرف كبير واحد على الأقل.";
            if (description.Contains("Passwords must have at least one lowercase", StringComparison.OrdinalIgnoreCase))
                return "كلمة المرور يجب أن تحتوي على حرف صغير واحد على الأقل.";
            if (description.Contains("Passwords must be at least", StringComparison.OrdinalIgnoreCase))
                return "كلمة المرور قصيرة جداً.";

            return description;
        }
    }
}
