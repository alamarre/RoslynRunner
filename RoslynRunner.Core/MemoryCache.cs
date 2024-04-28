using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RoslynRunner.Core;

public static class MemoryCache
{
	public static Dictionary<object, object> Cache = new Dictionary<object, object>();
}
